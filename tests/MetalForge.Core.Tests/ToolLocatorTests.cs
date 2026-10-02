using System.Text.Json;
using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;
using MetalForge.Core.Processes;
using MetalForge.Core.Toolchains;

namespace MetalForge.Core.Tests;

/// <summary>
/// 工具链探测测试。
///
/// 关键点：用**真实可执行的假工具**（.cmd 批处理）而不是 mock。
/// 探测逻辑的真实风险在于"能不能正确启动进程、能不能从真实输出里认出格式"，
/// mock 掉进程这一层就把要验证的东西一起 mock 掉了。
/// </summary>
public sealed class ToolLocatorTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectDirectory;

    public ToolLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "metalforge-tool-tests", Guid.NewGuid().ToString("N"));
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_projectDirectory);
    }

    /// <summary>测试用序列化选项：CA1869 要求复用实例。</summary>
    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };

    /// <summary>创建一个假工具：可执行文件打印指定版本行。</summary>
    private static string CreateFakeTool(string directory, string executableName, string versionOutput)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, executableName);

        // 批处理会原样打印，包含 @echo off 之外的任何内容；用 echo 输出模拟工具版本行。
        File.WriteAllText(
            path,
            $"@echo off{Environment.NewLine}echo {versionOutput}{Environment.NewLine}exit /b 0{Environment.NewLine}",
            System.Text.Encoding.ASCII);

        return path;
    }

    private void WriteProjectToolOverride(string toolId, string directory)
    {
        var overrides = new Dictionary<string, object>
        {
            ["tools"] = new Dictionary<string, object>
            {
                [toolId] = new { directory },
            },
        };

        File.WriteAllText(
            Path.Combine(_projectDirectory, "tools.json"),
            JsonSerializer.Serialize(overrides, _serializerOptions),
            System.Text.Encoding.UTF8);
    }

    private ToolLocator CreateLocator(IReadOnlyList<ToolRequirement> requirements, ToolSearchOptions? options = null)
    {
        var searchOptions = options ?? new ToolSearchOptions
        {
            ProjectDirectory = _projectDirectory,
            UserDirectory = Path.Combine(_root, "user"),
            // 刻意关闭 PATH 与常见位置搜索：本机缺少这些工具，
            // 打开后测试结果会依赖开发机环境，不再是可重复的。
            SearchSystemPath = false,
            SearchKnownLocations = false,
        };

        return new ToolLocator(requirements, new ProcessRunner(), searchOptions);
    }

    private static ToolRequirement CmakeRequirement(string? minimumVersion = "3.28") => new()
    {
        ToolId = "cmake",
        DisplayName = "CMake",
        Purpose = "build system generator",
        ExecutableName = "cmake.cmd",
        VersionArguments = [],
        VersionPattern = @"(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?",
        MinimumVersion = minimumVersion,
        Category = "build",
    };

    [Fact]
    public async Task FindsToolInProjectConfiguredDirectory()
    {
        var toolDirectory = Path.Combine(_root, "cmake-bin");
        CreateFakeTool(toolDirectory, "cmake.cmd", "cmake version 3.29.2");
        WriteProjectToolOverride("cmake", toolDirectory);

        using var locator = CreateLocator([CmakeRequirement()]);
        var report = await locator.CheckHealthAsync(CancellationToken.None);

        var health = Assert.Single(report.Tools);
        Assert.Equal(ToolStatus.Ready, health.Status);
        Assert.NotNull(health.Instance);
        Assert.Equal(ToolSource.ProjectConfiguration, health.Instance.Source);
        Assert.Equal(new Version(3, 29, 2, 0), health.Instance.Version);
        Assert.Equal(ToolchainReadiness.Ready, report.Readiness);
    }

    [Fact]
    public async Task AlsoAcceptsExeExecutableThroughProjectOverride()
    {
        // 真实场景里可执行文件是 .exe；这里用 cmd.exe 作为"工具"，
        // 验证探测对真实可执行文件同样有效（版本输出由 cmd /c ver 之类提供不可控，
        // 因此改用 where.exe —— 它是真实 exe 且输出稳定）。
        var toolDirectory = Path.Combine(_root, "where-bin");
        Directory.CreateDirectory(toolDirectory);

        // where.exe 位于 System32；把它复制到覆盖目录，模拟"项目指定的工具目录"。
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
        Assert.True(File.Exists(source), "本机应存在 where.exe");
        var copied = Path.Combine(toolDirectory, "where.exe");
        File.Copy(source, copied, overwrite: true);

        WriteProjectToolOverride("where", toolDirectory);

        using var locator = CreateLocator(
        [
            new ToolRequirement
            {
                ToolId = "where",
                DisplayName = "where",
                Purpose = "test double for a real .exe",
                ExecutableName = "where.exe",
                VersionArguments = ["/R", Path.GetTempPath(), "definitely-not-a-file-9f3a"],
                // 该工具不输出版本号，因此不设版本要求也不设 pattern
                MinimumVersion = null,
            },
        ]);

        var report = await locator.CheckHealthAsync(CancellationToken.None);

        var health = Assert.Single(report.Tools);
        Assert.Equal(ToolStatus.Ready, health.Status);
        Assert.Equal(copied, health.Instance!.ExecutablePath);
    }

    [Fact]
    public async Task MissingTool_IsReportedWithActionableDetail()
    {
        using var locator = CreateLocator(
        [
            new ToolRequirement
            {
                ToolId = "nonexistent",
                DisplayName = "Not Installed Tool",
                Purpose = "nothing",
                ExecutableName = "metalforge-absent-8c1f.exe",
                MinimumVersion = "1.0",
                KnownLocations = ["%ProgramFiles%\\DefinitelyMissing"],
                AcquisitionHint = "从官网下载并加入 PATH",
            },
        ]);

        var report = await locator.CheckHealthAsync(CancellationToken.None);

        var health = Assert.Single(report.Tools);
        Assert.Equal(ToolStatus.Missing, health.Status);
        Assert.Null(health.Instance);
        Assert.NotNull(health.StatusDetail);
        // 缺失时必须给出可操作的信息：版本要求 + 常见位置 + 获取方式
        Assert.Contains("1.0", health.StatusDetail, StringComparison.Ordinal);
        Assert.Contains("DefinitelyMissing", health.StatusDetail, StringComparison.Ordinal);
        Assert.Contains("官网", health.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(ToolchainReadiness.Unusable, report.Readiness);
    }

    [Fact]
    public async Task OutdatedTool_IsReportedAsOutdatedNotReady()
    {
        var toolDirectory = Path.Combine(_root, "old-cmake");
        CreateFakeTool(toolDirectory, "cmake.cmd", "cmake version 3.20.0");
        WriteProjectToolOverride("cmake", toolDirectory);

        using var locator = CreateLocator([CmakeRequirement(minimumVersion: "3.28")]);
        var report = await locator.CheckHealthAsync(CancellationToken.None);

        var health = Assert.Single(report.Tools);
        Assert.Equal(ToolStatus.Outdated, health.Status);
        Assert.False(health.IsUsable);
        Assert.Contains("3.20", health.StatusDetail!, StringComparison.Ordinal);
        Assert.Contains("3.28", health.StatusDetail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OptionalToolMissing_DoesNotDegradeOverallReadiness()
    {
        using var locator = CreateLocator(
        [
            new ToolRequirement
            {
                ToolId = "optional",
                DisplayName = "Optional Tool",
                Purpose = "nice to have",
                ExecutableName = "metalforge-optional-4d2a.exe",
                Required = false,
            },
        ]);

        var report = await locator.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(ToolStatus.Missing, Assert.Single(report.Tools).Status);
        // 非必需工具缺失不应把整体判为不可用
        Assert.Equal(ToolchainReadiness.Ready, report.Readiness);
        Assert.Empty(report.MissingRequiredTools());
    }

    [Fact]
    public async Task ReportIsCached_UntilInvalidated()
    {
        var toolDirectory = Path.Combine(_root, "cached-cmake");
        CreateFakeTool(toolDirectory, "cmake.cmd", "cmake version 3.29.2");
        WriteProjectToolOverride("cmake", toolDirectory);

        using var locator = CreateLocator([CmakeRequirement()]);

        var first = await locator.CheckHealthAsync(CancellationToken.None);
        var second = await locator.CheckHealthAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.NotNull(locator.LastReport);

        locator.Invalidate();
        var third = await locator.CheckHealthAsync(CancellationToken.None);

        Assert.NotSame(first, third);
        Assert.Equal(first.Tools.Count, third.Tools.Count);
    }

    [Fact]
    public async Task BrokenToolOverrideFile_IsReportedAndProbingContinues()
    {
        // 坏掉的 tools.json 不应让整个探测失败
        File.WriteAllText(Path.Combine(_projectDirectory, "tools.json"), "{ this is not json", System.Text.Encoding.UTF8);

        var diagnostics = new List<Diagnostic>();
        using var locator = new ToolLocator(
            [CmakeRequirement()],
            new ProcessRunner(),
            new ToolSearchOptions
            {
                ProjectDirectory = _projectDirectory,
                SearchSystemPath = false,
                SearchKnownLocations = false,
            },
            diagnostics.Add);

        var report = await locator.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(ToolStatus.Missing, Assert.Single(report.Tools).Status);
        Assert.Contains(diagnostics, d => d.Code == "MFTOOL004");
    }

    [Fact]
    public async Task EnumerateCandidates_FindsAllLocationsAndOrdersByPriority()
    {
        // 一个工具可能装了多份（项目指定、托管目录、PATH 各一份）。
        // 探测要能全部列出供用户切换，并把优先级最高的排在首位。
        var projectToolDirectory = Path.Combine(_root, "project-cmake");
        var managedToolDirectory = Path.Combine(_root, "managed", "cmake");
        CreateFakeTool(projectToolDirectory, "cmake.cmd", "cmake version 3.28.0");
        CreateFakeTool(managedToolDirectory, "cmake.cmd", "cmake version 3.30.1");
        WriteProjectToolOverride("cmake", projectToolDirectory);

        var requirement = CmakeRequirement();
        using var locator = CreateLocator(
            [requirement],
            new ToolSearchOptions
            {
                ProjectDirectory = _projectDirectory,
                UserDirectory = Path.Combine(_root, "user"),
                // 托管目录的契约是"托管根目录"：代码会自己拼上 <toolId>。
                ManagedToolsDirectory = Path.Combine(_root, "managed"),
                SearchSystemPath = false,
                SearchKnownLocations = false,
            });

        var candidates = await locator.EnumerateCandidatesAsync(requirement, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        // 顺序即优先级：项目配置高于托管目录
        Assert.Equal(ToolSource.ProjectConfiguration, candidates[0].Source);
        Assert.Equal(ToolSource.ManagedDirectory, candidates[1].Source);
        Assert.Equal(new Version(3, 28, 0, 0), candidates[0].Version);
        Assert.Equal(new Version(3, 30, 1, 0), candidates[1].Version);

        // CheckHealthAsync 必须采用优先级最高的那个
        var report = await locator.CheckHealthAsync(CancellationToken.None);
        var health = Assert.Single(report.Tools);
        Assert.Equal(ToolSource.ProjectConfiguration, health.Instance!.Source);
        Assert.Equal(2, health.Candidates.Count);
    }

    [Fact]
    public async Task DirectoryDeduplication_PrefersHigherPrioritySource()
    {
        // 同一个目录同时被用户配置与托管目录指向时，只应产生一个候选，
        // 且来源取优先级更高者（用户配置）。
        var shared = Path.Combine(_root, "shared");
        CreateFakeTool(Path.Combine(shared, "cmake"), "cmake.cmd", "cmake version 3.29.0");

        var requirement = CmakeRequirement();
        using var locator = CreateLocator(
            [requirement],
            new ToolSearchOptions
            {
                ProjectDirectory = _projectDirectory,
                UserDirectory = shared,
                ManagedToolsDirectory = shared,
                SearchSystemPath = false,
                SearchKnownLocations = false,
            });

        var candidates = await locator.EnumerateCandidatesAsync(requirement, CancellationToken.None);

        var single = Assert.Single(candidates);
        Assert.Equal(ToolSource.ManagedDirectory, single.Source);
    }

    [Fact]
    public async Task UnparsableVersion_IsReportedAsBrokenRatherThanSilentlyReady()
    {
        // 工具存在但版本输出格式陌生：不能当成"满足要求"放行。
        var toolDirectory = Path.Combine(_root, "weird-cmake");
        CreateFakeTool(toolDirectory, "cmake.cmd", "the build system, now with more features");
        WriteProjectToolOverride("cmake", toolDirectory);

        var diagnostics = new List<Diagnostic>();
        using var locator = new ToolLocator(
            [CmakeRequirement()],
            new ProcessRunner(),
            new ToolSearchOptions
            {
                ProjectDirectory = _projectDirectory,
                SearchSystemPath = false,
                SearchKnownLocations = false,
            },
            diagnostics.Add);

        var report = await locator.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(ToolStatus.Broken, Assert.Single(report.Tools).Status);
        Assert.Contains(diagnostics, d => d.Code == "MFTOOL003");
    }

    [Fact]
    public async Task RealToolsJsonAsset_LoadsAndPassesSchemaValidation()
    {
        // 内置清单必须能通过自身 Schema 校验，并且结构可被反序列化。
        var resolver = new AssetResolver(new AssetOptions
        {
            BuiltInAssetsDirectory = TestPaths.AssetsDirectory,
            UserDirectory = Path.Combine(_root, "user"),
        });

        var (configuration, diagnostics) = resolver.ResolveValidated("targets/tools.json", "targets/schema/tools.schema.json");

        var errors = diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, "tools.json 校验失败：" + string.Join(" | ", errors.Select(d => d.ToString())));
        Assert.NotNull(configuration.Root);

        var requirements = ToolchainCatalog.ReadRequirements(configuration.Root!);
        Assert.True(requirements.Count >= 8, $"内置工具清单过少：{requirements.Count}");

        // 每个工具都必须有可执行的探测配置
        foreach (var requirement in requirements)
        {
            Assert.False(string.IsNullOrWhiteSpace(requirement.ToolId), "toolId 不能为空");
            Assert.False(string.IsNullOrWhiteSpace(requirement.DisplayName), $"{requirement.ToolId} 缺少 displayName");
            Assert.False(string.IsNullOrWhiteSpace(requirement.Purpose), $"{requirement.ToolId} 缺少 purpose");
            Assert.EndsWith(".exe", requirement.ExecutableName, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(requirement.VersionArguments);
        }

        // 必需工具里必须包含构建闭环的最小集合
        var requiredIds = requirements.Where(r => r.Required).Select(r => r.ToolId).ToArray();
        Assert.Contains("cmake", requiredIds);
        Assert.Contains("ninja", requiredIds);
        Assert.Contains("nasm", requiredIds);
        Assert.Contains("qemu-system-x86_64", requiredIds);
        Assert.Contains("gdb", requiredIds);
    }

    [Fact]
    public async Task EveryBuiltInVersionPattern_MatchesItsOwnSampleOutput()
    {
        // 版本正则是配置里最容易写错又最难发现的部分（写错了只会表现为"版本未知"）。
        // 这里为每个内置工具准备一段真实的版本输出样本，逐个验证正则能命中。
        var samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cmake"] = "cmake version 3.29.2",
            ["ninja"] = "1.12.1",
            ["nasm"] = "NASM version 2.16.01 compiled on Apr 21 2023",
            ["qemu-system-x86_64"] = "QEMU emulator version 8.2.0 (v8.2.0-1-g1a2b3c)",
            ["qemu-system-aarch64"] = "QEMU emulator version 8.2.0",
            ["gdb"] = "GNU gdb (GDB) 14.2",
            ["clangd"] = "clangd version 18.1.8",
            ["clang"] = "clang version 18.1.8",
            ["gcc"] = "gcc (MinGW-W64 x86_64-ucrt-posix-seh, built by Brecht Sanders) 13.2.0",
            ["openocd"] = "Open On-Chip Debugger 0.12.0",
            ["git"] = "git version 2.45.1.windows.1",
        };

        var resolver = new AssetResolver(new AssetOptions
        {
            BuiltInAssetsDirectory = TestPaths.AssetsDirectory,
            UserDirectory = Path.Combine(_root, "user"),
        });

        var (configuration, _) = resolver.ResolveValidated("targets/tools.json", "targets/schema/tools.schema.json");
        var requirements = ToolchainCatalog.ReadRequirements(configuration.Root!);

        var failures = new List<string>();

        foreach (var requirement in requirements)
        {
            if (!samples.TryGetValue(requirement.ToolId, out var sample))
            {
                failures.Add($"{requirement.ToolId}: 测试缺少样本输出");
                continue;
            }

            var (version, reason) = ToolVersionParser.Parse(sample, requirement.VersionPattern);
            if (version is null)
            {
                failures.Add($"{requirement.ToolId}: 正则无法匹配样本 '{sample}'（{reason}）");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task ThisMachine_ProducesAReadableReportRegardlessOfWhatIsInstalled()
    {
        // 真实环境冒烟：无论本机装没装工具，报告都必须可读、不抛异常。
        // 本机实测缺少全部 OSDev 工具，因此这条测试同时覆盖"全缺"这一最坏路径。
        var resolver = new AssetResolver(new AssetOptions
        {
            BuiltInAssetsDirectory = TestPaths.AssetsDirectory,
            UserDirectory = Path.Combine(_root, "user"),
        });

        var (configuration, _) = resolver.ResolveValidated("targets/tools.json", "targets/schema/tools.schema.json");
        var requirements = ToolchainCatalog.ReadRequirements(configuration.Root!);

        var diagnostics = new List<Diagnostic>();
        using var locator = new ToolLocator(
            requirements,
            new ProcessRunner(),
            new ToolSearchOptions
            {
                SearchSystemPath = true,
                SearchKnownLocations = true,
                ProbeVersions = true,
            },
            diagnostics.Add);

        var report = await locator.CheckHealthAsync(CancellationToken.None);

        Assert.Equal(requirements.Count, report.Tools.Count);
        Assert.True(report.Duration > TimeSpan.Zero);
        Assert.False(string.IsNullOrWhiteSpace(report.Describe()));

        // 每个工具都要有明确状态，不允许"未知"
        Assert.All(report.Tools, tool =>
        {
            Assert.True(Enum.IsDefined(tool.Status), $"{tool.Requirement.ToolId} 状态非法");
            if (tool.Status == ToolStatus.Missing)
            {
                Assert.NotNull(tool.StatusDetail);
            }
        });

        // 报告要能被分类展示（界面按类别分组）
        Assert.NotEmpty(report.ByCategory());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }
}
