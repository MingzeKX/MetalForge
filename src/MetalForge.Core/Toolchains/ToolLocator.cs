using MetalForge.Core.Diagnostics;
using MetalForge.Core.Processes;

namespace MetalForge.Core.Toolchains;

/// <summary>工具链探测的搜索路径配置。</summary>
public sealed record ToolSearchOptions
{
    /// <summary>项目目录；其下的 <c>.metalforge/tools.json</c> 可显式指定工具路径。</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>用户配置目录（<c>%APPDATA%\MetalForge</c> 或便携模式下的程序目录）。</summary>
    public string? UserDirectory { get; init; }

    /// <summary>MetalForge 托管工具目录（应用内下载安装的工具）。</summary>
    public string? ManagedToolsDirectory { get; init; }

    /// <summary>是否搜索系统 PATH。默认 true。</summary>
    public bool SearchSystemPath { get; init; } = true;

    /// <summary>是否搜索常见安装位置白名单。默认 true。</summary>
    public bool SearchKnownLocations { get; init; } = true;

    /// <summary>单个工具的版本命令超时。有些工具在无参数时会挂起，必须有上限。</summary>
    public TimeSpan VersionProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>是否执行版本命令。关闭时只做存在性检查（用于极快启动路径）。</summary>
    public bool ProbeVersions { get; init; } = true;
}

/// <summary>工具链探测服务。</summary>
public interface IToolLocator : IDisposable
{
    /// <summary>期望的工具清单（来自配置）。</summary>
    IReadOnlyList<ToolRequirement> Requirements { get; }

    /// <summary>
    /// 探测全部工具。结果内部缓存；调用 <see cref="Invalidate"/> 后重新探测。
    /// </summary>
    Task<ToolHealthReport> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary>枚举某个工具的全部候选实例（不受缓存影响，用于"切换到另一个版本"）。</summary>
    Task<IReadOnlyList<ToolInstance>> EnumerateCandidatesAsync(ToolRequirement requirement, CancellationToken cancellationToken);

    /// <summary>丢弃缓存，下次调用重新探测。</summary>
    void Invalidate();

    /// <summary>最近一次报告；尚未探测时为 null。</summary>
    ToolHealthReport? LastReport { get; }
}

/// <summary>
/// <see cref="IToolLocator"/> 的默认实现。
///
/// 探测顺序（DESIGN.md §6）：项目配置 → 用户配置 → 托管目录 → PATH → 常见安装位置。
/// 命中即采用优先级最高的一个，同时保留全部候选，供用户手动切换。
///
/// 探测结果会缓存：版本命令要启动进程，每次刷新 UI 都跑一遍会让界面卡顿。
/// </summary>
public sealed class ToolLocator : IToolLocator
{
    private readonly ToolSearchOptions _options;
    private readonly IProcessRunner _processRunner;
    private readonly Action<Diagnostic>? _diagnosticSink;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ToolHealthReport? _report;
    private bool _disposed;

    public ToolLocator(
        IReadOnlyList<ToolRequirement> requirements,
        IProcessRunner processRunner,
        ToolSearchOptions? options = null,
        Action<Diagnostic>? diagnosticSink = null)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(processRunner);

        Requirements = requirements;
        _processRunner = processRunner;
        _options = options ?? new ToolSearchOptions();
        _diagnosticSink = diagnosticSink;
    }

    public IReadOnlyList<ToolRequirement> Requirements { get; }

    public ToolHealthReport? LastReport => _report;

    public void Invalidate() => _report = null;

    public async Task<ToolHealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (_report is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 双检：等锁期间可能已被另一个调用填好。
            if (_report is { } filled)
            {
                return filled;
            }

            var startedAt = DateTimeOffset.Now;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var tools = new List<ToolHealth>(Requirements.Count);

            foreach (var requirement in Requirements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tools.Add(await CheckOneAsync(requirement, cancellationToken).ConfigureAwait(false));
            }

            stopwatch.Stop();

            var report = new ToolHealthReport
            {
                Tools = tools,
                Readiness = DetermineReadiness(tools),
                CheckedAt = startedAt,
                Duration = stopwatch.Elapsed,
            };

            _report = report;
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ToolInstance>> EnumerateCandidatesAsync(ToolRequirement requirement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        var candidates = new List<ToolInstance>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (directory, source) in EnumerateSearchDirectories(requirement))
        {
            var path = Path.Combine(directory, requirement.ExecutableName);
            if (!File.Exists(path) || !seenPaths.Add(path))
            {
                continue;
            }

            var instance = await ProbeAsync(requirement, path, source, cancellationToken).ConfigureAwait(false);
            candidates.Add(instance);
        }

        // PATH 上的可执行文件可能位于我们也已扫描的目录中，用完整路径去重。
        if (_options.SearchSystemPath)
        {
            foreach (var path in EnumerateFromPath(requirement.ExecutableName))
            {
                if (!seenPaths.Add(path))
                {
                    continue;
                }

                var instance = await ProbeAsync(requirement, path, ToolSource.SystemPath, cancellationToken).ConfigureAwait(false);
                candidates.Add(instance);
            }
        }

        return candidates;
    }

    private async Task<ToolHealth> CheckOneAsync(ToolRequirement requirement, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<ToolInstance> candidates;
        try
        {
            candidates = await EnumerateCandidatesAsync(requirement, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 探测目录不可读不应该让整个报告失败：报一个 Broken 状态并继续。
            Report(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFTOOL001",
                $"探测 {requirement.DisplayName} 时访问文件系统失败：{exception.Message}",
                Hint: "该工具状态未知；请检查目录权限。",
                Exception: exception));

            stopwatch.Stop();
            return new ToolHealth
            {
                Requirement = requirement,
                Status = ToolStatus.Broken,
                StatusDetail = $"探测失败：{exception.Message}",
                ProbeDuration = stopwatch.Elapsed,
            };
        }

        stopwatch.Stop();

        if (candidates.Count == 0)
        {
            return new ToolHealth
            {
                Requirement = requirement,
                Status = ToolStatus.Missing,
                StatusDetail = BuildMissingDetail(requirement),
                ProbeDuration = stopwatch.Elapsed,
            };
        }

        var chosen = candidates[0];
        var minimum = requirement.ParsedMinimumVersion;
        var satisfaction = ToolVersionParser.Satisfies(chosen.Version, minimum);

        var (status, detail) = satisfaction switch
        {
            VersionSatisfaction.Satisfied or VersionSatisfaction.NoRequirement =>
                (ToolStatus.Ready, (string?)null),
            VersionSatisfaction.TooOld =>
                (ToolStatus.Outdated, $"已安装 {chosen.Version}，低于要求的 {minimum}。"),
            _ =>
                (ToolStatus.Broken, "已找到可执行文件，但无法解析其版本号；请手动确认该工具可用。"),
        };

        return new ToolHealth
        {
            Requirement = requirement,
            Instance = chosen,
            Candidates = candidates,
            Status = status,
            StatusDetail = detail,
            ProbeDuration = stopwatch.Elapsed,
        };
    }

    private async Task<ToolInstance> ProbeAsync(
        ToolRequirement requirement,
        string executablePath,
        ToolSource source,
        CancellationToken cancellationToken)
    {
        if (!_options.ProbeVersions)
        {
            return new ToolInstance(requirement.ToolId, executablePath, Version: null, source);
        }

        var request = new ProcessRequest
        {
            FileName = executablePath,
            Arguments = requirement.VersionArguments,
            Timeout = _options.VersionProbeTimeout,
            Description = $"探测 {requirement.DisplayName} 版本",
        };

        ProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(request, progress: null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Report(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFTOOL002",
                $"执行 {requirement.DisplayName} 的版本命令时失败：{exception.Message}",
                executablePath,
                Hint: "该工具可能损坏或缺少运行库；已保留候选但版本未知。",
                Exception: exception));

            return new ToolInstance(requirement.ToolId, executablePath, Version: null, source);
        }

        if (result.FailedToStart)
        {
            return new ToolInstance(requirement.ToolId, executablePath, Version: null, source);
        }

        var output = result.CombinedOutput;
        var (version, failureReason) = ToolVersionParser.Parse(output, requirement.VersionPattern);

        if (version is null && failureReason is not null)
        {
            // 版本解析失败很常见（工具输出格式变了），记为 Info 而不是 Warning：
            // 这不是环境错误，只是我们认不出格式，用户无需处理。
            Report(new Diagnostic(
                DiagnosticSeverity.Info,
                "MFTOOL003",
                $"无法从 {requirement.DisplayName} 的输出解析版本：{failureReason}",
                executablePath,
                Hint: "工具本身可用；如需精确版本判定，请在其描述中补充 versionPattern。"));
        }

        return new ToolInstance(requirement.ToolId, executablePath, version, source, output);
    }

    private IEnumerable<(string Directory, ToolSource Source)> EnumerateSearchDirectories(ToolRequirement requirement)
    {
        // 顺序即优先级。项目配置与用户配置指定的是"目录"，用于覆盖探测结果。
        foreach (var directory in ReadConfiguredDirectories(_options.ProjectDirectory, requirement.ToolId))
        {
            yield return (directory, ToolSource.ProjectConfiguration);
        }

        foreach (var directory in ReadConfiguredDirectories(_options.UserDirectory, requirement.ToolId))
        {
            yield return (directory, ToolSource.UserConfiguration);
        }

        if (!string.IsNullOrWhiteSpace(_options.ManagedToolsDirectory))
        {
            yield return (Path.Combine(_options.ManagedToolsDirectory, requirement.ToolId), ToolSource.ManagedDirectory);
            yield return (_options.ManagedToolsDirectory, ToolSource.ManagedDirectory);
        }

        if (_options.SearchKnownLocations)
        {
            foreach (var location in requirement.KnownLocations)
            {
                var expanded = Environment.ExpandEnvironmentVariables(location);
                if (Directory.Exists(expanded))
                {
                    yield return (expanded, ToolSource.KnownLocation);
                }
            }
        }
    }

    /// <summary>
    /// 读取 <c>tools.json</c> 形式的覆盖配置。
    /// 结构：<c>{ "tools": { "cmake": { "directory": "C:\\tools\\cmake\\bin" } } }</c>。
    /// 该文件可损坏：损坏时只记录诊断并忽略，不影响其余探测。
    /// </summary>
    private IEnumerable<string> ReadConfiguredDirectories(string? root, string toolId)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            yield break;
        }

        var path = Path.Combine(root, "tools.json");
        if (!File.Exists(path))
        {
            yield break;
        }

        System.Text.Json.Nodes.JsonNode? node;
        try
        {
            node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            Report(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFTOOL004",
                $"工具覆盖配置无法读取，已忽略：{exception.Message}",
                path,
                Hint: "该文件用于手动指定工具路径；修正后重新探测即可。",
                Exception: exception));
            yield break;
        }

        if (node?["tools"]?[toolId]?["directory"]?.GetValue<string>() is not { Length: > 0 } directory)
        {
            yield break;
        }

        yield return directory;
    }

    private static IEnumerable<string> EnumerateFromPath(string executableName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            yield break;
        }

        foreach (var rawDirectory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // PATH 中常有引号包裹的目录（安装程序写入不规范）。
            var directory = rawDirectory.Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(directory, executableName);
            }
            catch (ArgumentException)
            {
                // 非法路径段（例如包含通配符）直接跳过，不让整个探测失败。
                continue;
            }

            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static string BuildMissingDetail(ToolRequirement requirement)
    {
        var parts = new List<string>();

        if (requirement.ParsedMinimumVersion is { } minimum)
        {
            parts.Add($"要求 {minimum} 或更高");
        }

        if (requirement.KnownLocations.Count > 0)
        {
            var locations = requirement.KnownLocations.Select(Environment.ExpandEnvironmentVariables);
            parts.Add("常见位置：" + string.Join("、", locations));
        }

        if (requirement.AcquisitionHint is { Length: > 0 } hint)
        {
            parts.Add(hint);
        }

        return parts.Count == 0 ? "未在配置目录、PATH 与常见位置中找到。" : string.Join("；", parts);
    }

    private static ToolchainReadiness DetermineReadiness(IReadOnlyList<ToolHealth> tools)
    {
        var required = tools.Where(tool => tool.Requirement.Required).ToArray();
        if (required.Length == 0)
        {
            return ToolchainReadiness.Ready;
        }

        var usable = required.Count(tool => tool.IsUsable);

        if (usable == required.Length)
        {
            return ToolchainReadiness.Ready;
        }

        return usable == 0 ? ToolchainReadiness.Unusable : ToolchainReadiness.Degraded;
    }

    private void Report(Diagnostic diagnostic)
    {
        if (_diagnosticSink is not null)
        {
            _diagnosticSink(diagnostic);
            return;
        }

        System.Diagnostics.Trace.WriteLine(diagnostic.ToString());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
        _report = null;
    }
}
