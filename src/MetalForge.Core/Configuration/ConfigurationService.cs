using System.Text.Json;
using MetalForge.Core.Diagnostics;
using MetalForge.Core.Theming;

namespace MetalForge.Core.Configuration;

/// <summary>品牌与关于信息（<c>assets/branding/*.json</c>）的强类型模型。</summary>
public sealed record BrandingLink
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string? Uri { get; init; }
}

public sealed record WindowBranding
{
    public string Title { get; init; } = "MetalForge";
    public string TitleTemplate { get; init; } = "{document}{dirtyIndicator} — {app}";
    public int DefaultWidth { get; init; } = 1280;
    public int DefaultHeight { get; init; } = 720;
    public int MinimumWidth { get; init; } = 1024;
    public int MinimumHeight { get; init; } = 640;

    /// <summary>启动时是否最大化。小屏幕或小窗口习惯的用户可以改这里。</summary>
    public bool StartMaximized { get; init; }

    public bool RememberSizeAndPosition { get; init; } = true;
}

public sealed record SplashBranding
{
    public bool Enabled { get; init; } = true;
    public bool ShowWhileLoading { get; init; } = true;
    public int MinimumDurationMilliseconds { get; init; } = 400;
    public string? Image { get; init; }

    /// <summary>启动画面背景色。留空时使用当前主题的 background，避免在 Core 里出现颜色字面量。</summary>
    public string? BackgroundColor { get; init; }

    /// <summary>启动画面前景色。留空时使用当前主题的 foreground。</summary>
    public string? ForegroundColor { get; init; }

    public bool ShowVersion { get; init; } = true;
    public bool ShowStatus { get; init; } = true;
}

public sealed record BrandingAssets
{
    public string? Icon { get; init; }
    public string? Logo { get; init; }
    public string? LogoMonochrome { get; init; }
    public string Theme { get; init; } = "metalforge-dark";
    public string Layout { get; init; } = "default";
}

/// <summary>应用品牌定义。全部字段可由 <c>assets/branding/app.json</c> 覆盖而不需重新编译。</summary>
public sealed record AppBranding
{
    public string Id { get; init; } = "metalforge";
    public string Name { get; init; } = "MetalForge";
    public string Publisher { get; init; } = "MetalForge Project";
    public string Version { get; init; } = "0.0.0";
    public string? Tagline { get; init; }
    public string? Description { get; init; }
    public string? Copyright { get; init; }
    public IReadOnlyList<BrandingLink> Links { get; init; } = [];
    public WindowBranding Window { get; init; } = new();
    public SplashBranding Splash { get; init; } = new();
    public BrandingAssets Assets { get; init; } = new();

    /// <summary>按布局模板生成窗口标题，例如 <c>kernel.c — MetalForge</c>。</summary>
    /// <param name="documentName">当前文档名；为 null 表示没有打开的文档。</param>
    /// <param name="isDirty">是否有未保存改动。</param>
    public string FormatWindowTitle(string? documentName = null, bool isDirty = false)
    {
        // 无文档时只用应用名，避免出现 "MetalForge — MetalForge"。
        if (string.IsNullOrEmpty(documentName))
        {
            return Name;
        }

        var template = string.IsNullOrWhiteSpace(Window.TitleTemplate) ? "{app}" : Window.TitleTemplate;
        return template
            .Replace("{app}", Name, StringComparison.Ordinal)
            .Replace("{document}", documentName, StringComparison.Ordinal)
            .Replace("{dirtyIndicator}", isDirty ? " •" : string.Empty, StringComparison.Ordinal)
            .Trim();
    }
}

public sealed record AboutCredit
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string? Uri { get; init; }
}

public sealed record AboutSection
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
}

public sealed record AboutBuildInfo
{
    public bool ShowRuntime { get; init; } = true;
    public bool ShowPlatform { get; init; } = true;
    public bool ShowCommit { get; init; } = true;
    public bool ShowBuildDate { get; init; } = true;
}

/// <summary>"关于"对话框内容。</summary>
public sealed record AboutInformation
{
    public string Title { get; init; } = "关于";
    public string? Headline { get; init; }
    public string? Subheadline { get; init; }
    public string? Description { get; init; }
    public AboutBuildInfo BuildInfo { get; init; } = new();
    public IReadOnlyList<AboutCredit> Credits { get; init; } = [];
    public IReadOnlyList<AboutSection> Sections { get; init; } = [];
}

public sealed record LicenseDocument
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Spdx { get; init; }
    public bool IsPrimary { get; init; }
    public string Text { get; init; } = string.Empty;
}

public sealed record LicenseInformation
{
    public string Title { get; init; } = "许可协议";
    public string? Introduction { get; init; }
    public IReadOnlyList<LicenseDocument> Documents { get; init; } = [];
}

public sealed record Contributor
{
    public string Name { get; init; } = string.Empty;
    public string Role { get; init; } = "contributor";
    public string? Note { get; init; }
    public string? Uri { get; init; }
}

public sealed record ContributorList
{
    public string Title { get; init; } = "贡献者";
    public IReadOnlyDictionary<string, string> Roles { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<Contributor> People { get; init; } = [];
}

public sealed record ChangelogEntry
{
    public string Category { get; init; } = "changed";
    public string Text { get; init; } = string.Empty;
}

public sealed record ChangelogRelease
{
    public string Version { get; init; } = string.Empty;
    public string? Date { get; init; }
    public string? Channel { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<ChangelogEntry> Entries { get; init; } = [];
}

public sealed record Changelog
{
    public string Title { get; init; } = "更新日志";
    public IReadOnlyDictionary<string, string> Categories { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ChangelogRelease> Releases { get; init; } = [];
}

public sealed record UpdateSigning
{
    public bool Required { get; init; } = true;
    public string? PublicKeyPemFile { get; init; }
    public string Algorithm { get; init; } = "RSA-SHA256";
}

public sealed record UpdateBehavior
{
    public bool DownloadAutomatically { get; init; }
    public bool InstallAutomatically { get; init; }
    public bool NotifyOnly { get; init; } = true;
    public bool AllowUserToDisable { get; init; } = true;
}

/// <summary>更新检查配置。默认仅提示，绝不后台静默下载。</summary>
public sealed record UpdateOptions
{
    public bool Enabled { get; init; } = true;
    public bool CheckOnStartup { get; init; } = true;
    public string Channel { get; init; } = "stable";
    public IReadOnlyList<string> Channels { get; init; } = ["stable", "beta", "alpha"];
    public string? Endpoint { get; init; }
    public int RequestTimeoutSeconds { get; init; } = 10;
    public string ChangelogSource { get; init; } = "branding/changelog.json";
    public UpdateSigning Signing { get; init; } = new();
    public UpdateBehavior Behavior { get; init; } = new();
}

/// <summary>一次完整配置加载的结果快照。所有字段都是不可变的，便于整体原子替换。</summary>
public sealed record MetalForgeConfiguration
{
    public required AppBranding Branding { get; init; }
    public required AboutInformation About { get; init; }
    public required LicenseInformation Licenses { get; init; }
    public required ContributorList Contributors { get; init; }
    public required Changelog Changelog { get; init; }
    public required UpdateOptions Update { get; init; }
    public required ThemeDefinition Theme { get; init; }
    public required IReadOnlyList<ThemeDefinition> AvailableThemes { get; init; }

    /// <summary>可用布局预设（来自 <c>assets/layouts/*.layout.json</c>）。</summary>
    public IReadOnlyList<Layout.LayoutPreset> AvailableLayouts { get; init; } = [];

    /// <summary>
    /// 最近一次工具链探测结果。
    /// 探测是异步且可能较慢的（要启动外部进程），因此不在这里同步执行；
    /// 启动后由宿主设置，界面据此显示状态栏摘要。
    /// </summary>
    public Toolchains.ToolHealthReport? ToolHealth { get; init; }

    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>是否有任何一层配置解析失败（已降级）。</summary>
    public bool HasErrors => Diagnostics.Any(d => d.Severity >= DiagnosticSeverity.Error);

    /// <summary>内置兜底配置：当 assets/ 完全不可用时保证程序仍能启动（唯一允许的硬编码后备）。</summary>
    public static MetalForgeConfiguration Fallback { get; } = new()
    {
        Branding = new AppBranding(),
        About = new AboutInformation(),
        Licenses = new LicenseInformation(),
        Contributors = new ContributorList(),
        Changelog = new Changelog(),
        Update = new UpdateOptions(),
        Theme = new ThemeDefinition(),
        AvailableThemes = [new ThemeDefinition()],
        AvailableLayouts = [],
        Diagnostics = [],
    };
}

/// <summary>配置服务：负责加载、热重载、主题切换，并把诊断交给 UI 呈现。</summary>
public interface IConfigurationService : IDisposable
{
    /// <summary>当前生效的配置快照。</summary>
    MetalForgeConfiguration Current { get; }

    /// <summary>当前主题（等价于 <c>Current.Theme</c>，便于绑定）。</summary>
    ThemeDefinition CurrentTheme { get; }

    /// <summary>可用主题（来自 <c>assets/themes/*.theme.json</c>）。</summary>
    IReadOnlyList<ThemeDefinition> AvailableThemes { get; }

    /// <summary>可用布局预设（来自 <c>assets/layouts/*.layout.json</c>）。</summary>
    IReadOnlyList<Layout.LayoutPreset> AvailableLayouts { get; }

    /// <summary>最近一次工具链探测结果；尚未探测时为 null。</summary>
    Toolchains.ToolHealthReport? ToolHealth { get; }

    /// <summary>配置被重新加载后触发（热重载或手动刷新）。</summary>
    event EventHandler<MetalForgeConfiguration>? ConfigurationChanged;

    /// <summary>开始监视配置文件变化以实现热重载。可重复调用，重复调用无副作用。</summary>
    void StartWatching();

    /// <summary>从磁盘重新加载全部配置。</summary>
    Task ReloadAsync(CancellationToken cancellationToken);

    /// <summary>切换主题；<paramref name="themeId"/> 不存在时保留当前主题并返回 false。</summary>
    bool TryApplyTheme(string themeId);

    /// <summary>
    /// 记录一次工具链探测结果（由宿主在探测完成后调用）。
    /// 探测本身不在配置服务里做：它需要启动外部进程，属于另一条职责。
    /// </summary>
    void SetToolHealth(Toolchains.ToolHealthReport report);

    /// <summary>把某个内置资源的路径解析为绝对路径，并在用户/项目层存在覆盖时优先返回覆盖文件。</summary>
    string? ResolveAssetPath(string relativePath);

    /// <summary>读取资源文件的文本内容（用于显示 SVG/文本类资源）。</summary>
    Task<string?> ReadAssetTextAsync(string relativePath, CancellationToken cancellationToken);

    /// <summary>读取资源文件的字节内容（用于图标、字体等二进制资源）。</summary>
    Task<byte[]?> ReadAssetBytesAsync(string relativePath, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IConfigurationService"/> 的默认实现。
/// 加载策略：任一层失败都不阻断启动，失败层被忽略并记录诊断。
/// </summary>
public sealed class ConfigurationService : IConfigurationService
{
    private const string AppAssetPath = "branding/app.json";
    private const string AboutAssetPath = "branding/about.json";
    private const string LicensesAssetPath = "branding/licenses.json";
    private const string ContributorsAssetPath = "branding/contributors.json";
    private const string ChangelogAssetPath = "branding/changelog.json";
    private const string UpdateAssetPath = "branding/update.json";
    private const string ThemeSchemaPath = "themes/schema/theme.schema.json";

    private readonly AssetResolver _resolver;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _gate = new();
    private Timer? _debounceTimer;
    private bool _disposed;

    public ConfigurationService(AssetResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        Current = MetalForgeConfiguration.Fallback;
    }

    public MetalForgeConfiguration Current { get; private set; }

    public ThemeDefinition CurrentTheme => Current.Theme;

    public IReadOnlyList<ThemeDefinition> AvailableThemes => Current.AvailableThemes;

    public IReadOnlyList<Layout.LayoutPreset> AvailableLayouts => Current.AvailableLayouts;

    public Toolchains.ToolHealthReport? ToolHealth => Current.ToolHealth;

    public event EventHandler<MetalForgeConfiguration>? ConfigurationChanged;

    /// <summary>开始监视配置文件变化，实现热重载。</summary>
    public void StartWatching()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_watchers.Count > 0)
            {
                return;
            }

            foreach (var directory in EnumerateWatchDirectories())
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    Filter = "*.json",
                    EnableRaisingEvents = true,
                };

                watcher.Changed += OnAssetChanged;
                watcher.Created += OnAssetChanged;
                watcher.Deleted += OnAssetChanged;
                watcher.Renamed += OnAssetRenamed;
                _watchers.Add(watcher);
            }
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = await Task.Run(LoadCore, cancellationToken).ConfigureAwait(false);

        var previous = Current;
        Current = result;

        if (!ReferenceEquals(previous, result))
        {
            ConfigurationChanged?.Invoke(this, result);
        }
    }

    public bool TryApplyTheme(string themeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(themeId);

        var theme = Current.AvailableThemes.FirstOrDefault(t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase));
        if (theme is null || ReferenceEquals(theme, Current.Theme))
        {
            return theme is not null;
        }

        var updated = Current with { Theme = theme };
        Current = updated;
        ConfigurationChanged?.Invoke(this, updated);
        return true;
    }

    public void SetToolHealth(Toolchains.ToolHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        // 探测结果不属于"配置"，因此不参与热重载；重新加载配置时保留它。
        var updated = Current with { ToolHealth = report };
        Current = updated;
        ConfigurationChanged?.Invoke(this, updated);
    }

    public string? ResolveAssetPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string? best = null;
        var normalized = relativePath.Replace('\\', '/');

        if (_resolver.ProjectDirectory is not null)
        {
            var projectOverlay = Path.Combine(_resolver.ProjectDirectory, ".metalforge", normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(projectOverlay))
            {
                best = projectOverlay;
            }
        }

        if (best is null)
        {
            var userAsset = Path.Combine(_resolver.UserDirectory, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(userAsset))
            {
                best = userAsset;
            }
        }

        if (best is null)
        {
            var builtIn = Path.Combine(_resolver.BuiltInDirectory, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(builtIn))
            {
                best = builtIn;
            }
        }

        return best;
    }

    public async Task<string?> ReadAssetTextAsync(string relativePath, CancellationToken cancellationToken)
    {
        var path = ResolveAssetPath(relativePath);
        return path is null ? null : await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> ReadAssetBytesAsync(string relativePath, CancellationToken cancellationToken)
    {
        var path = ResolveAssetPath(relativePath);
        return path is null ? null : await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private MetalForgeConfiguration LoadCore()
    {
        var diagnostics = new List<Diagnostic>();

        var branding = LoadModel<AppBranding>(AppAssetPath, "branding/schema/app.schema.json", diagnostics) ?? new AppBranding();
        var about = LoadModel<AboutInformation>(AboutAssetPath, "branding/schema/about.schema.json", diagnostics) ?? new AboutInformation();
        var licenses = LoadModel<LicenseInformation>(LicensesAssetPath, "branding/schema/licenses.schema.json", diagnostics) ?? new LicenseInformation();
        var contributors = LoadModel<ContributorList>(ContributorsAssetPath, "branding/schema/contributors.schema.json", diagnostics) ?? new ContributorList();
        var changelog = LoadModel<Changelog>(ChangelogAssetPath, "branding/schema/changelog.schema.json", diagnostics) ?? new Changelog();
        var update = LoadModel<UpdateOptions>(UpdateAssetPath, "branding/schema/update.schema.json", diagnostics) ?? new UpdateOptions();

        var themes = LoadThemes(diagnostics);
        var themeId = branding.Assets.Theme;
        var theme = themes.FirstOrDefault(t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase))
                    ?? (themes.Count > 0 ? themes[0] : null)
                    ?? new ThemeDefinition();

        var layouts = LoadLayouts(diagnostics);

        return new MetalForgeConfiguration
        {
            Branding = branding,
            About = about,
            Licenses = licenses,
            Contributors = contributors,
            Changelog = changelog,
            Update = update,
            Theme = theme,
            AvailableThemes = themes,
            AvailableLayouts = layouts,
            // 工具链探测与配置文件无关，重载时保留上一次结果。
            ToolHealth = Current.ToolHealth,
            Diagnostics = diagnostics,
        };
    }

    private IReadOnlyList<Layout.LayoutPreset> LoadLayouts(List<Diagnostic> diagnostics)
    {
        var loader = new Layout.LayoutLoader(_resolver);
        var (presets, layoutDiagnostics) = loader.LoadAll();
        diagnostics.AddRange(layoutDiagnostics);

        // 布局引用的标签页必须已登记；未登记的只会让某个面板变空，
        // 因此在这里就报出来（Core 侧还有 TabCatalogTests 兜底）。
        foreach (var preset in presets)
        {
            foreach (var tabId in Layout.TabCatalog.FindUnregisteredTabs(preset))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "MFLAYOUT005",
                    $"布局 '{preset.Id}' 引用了未登记的标签页 '{tabId}'，该面板将显示为占位内容。",
                    Hint: "检查 id 拼写，或在 TabCatalog 中登记该标签页。"));
            }
        }

        return presets;
    }

    private T? LoadModel<T>(string relativePath, string? schemaRelativePath, List<Diagnostic> diagnostics)
        where T : class
    {
        var (configuration, layerDiagnostics) = _resolver.ResolveValidated(relativePath, schemaRelativePath);
        diagnostics.AddRange(layerDiagnostics);

        if (configuration.Root is null)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFCFG150",
                $"未找到配置文件 {relativePath}，该部分使用内置默认值。",
                relativePath,
                Hint: "确认 assets/ 目录完整；这不会阻止程序启动。"));
            return null;
        }

        var model = configuration.Bind<T>(AssetOptions.SerializerOptions, out var failure);
        if (failure is not null)
        {
            diagnostics.Add(failure with { Source = relativePath });
        }

        return model;
    }

    private List<ThemeDefinition> LoadThemes(List<Diagnostic> diagnostics)
    {
        var themes = new List<ThemeDefinition>();
        var byId = new Dictionary<string, ThemeDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileName in _resolver.Enumerate("themes", "*.theme.json"))
        {
            var relativePath = "themes/" + fileName;
            var (configuration, layerDiagnostics) = _resolver.ResolveValidated(relativePath, ThemeSchemaPath);
            diagnostics.AddRange(layerDiagnostics);

            if (configuration.Root is null)
            {
                continue;
            }

            var theme = configuration.Bind<ThemeDefinition>(AssetOptions.SerializerOptions, out var failure);
            if (failure is not null)
            {
                diagnostics.Add(failure with { Source = relativePath });
                continue;
            }

            if (theme is null)
            {
                continue;
            }

            byId[theme.Id] = theme;
            themes.Add(theme);
        }

        // 解析 extends：最多两轮，避免循环继承导致无限递归。
        for (var pass = 0; pass < 2; pass++)
        {
            for (var index = 0; index < themes.Count; index++)
            {
                var theme = themes[index];
                if (theme.Extends is null || !byId.TryGetValue(theme.Extends, out var parent))
                {
                    continue;
                }

                themes[index] = parent.WithOverlay(theme);
            }
        }

        return themes;
    }

    private IEnumerable<string> EnumerateWatchDirectories()
    {
        if (_resolver.ProjectDirectory is not null)
        {
            yield return Path.Combine(_resolver.ProjectDirectory, ".metalforge");
        }

        yield return _resolver.UserDirectory;
        yield return _resolver.BuiltInDirectory;
    }

    private void OnAssetChanged(object sender, FileSystemEventArgs e)
    {
        ScheduleReload();
    }

    private void OnAssetRenamed(object sender, RenamedEventArgs e)
    {
        ScheduleReload();
    }

    private void ScheduleReload()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // 300ms 防抖：编辑器保存一次配置会触发多个事件。
            _debounceTimer ??= new Timer(_ => _ = ReloadAfterChangeAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ReloadAfterChangeAsync()
    {
        try
        {
            await ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // 热重载失败不能拖垮应用：把问题作为诊断暴露给用户，保留上一份可用快照。
            var diagnostics = Current.Diagnostics
                .Where(d => d.Code != "MFCFG160")
                .Append(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "MFCFG160",
                    $"配置热重载失败：{exception.Message}",
                    Hint: "已保留上一份可用配置；修正文件后会自动重试。",
                    Exception: exception))
                .ToArray();

            Current = Current with { Diagnostics = diagnostics };
            ConfigurationChanged?.Invoke(this, Current);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnAssetChanged;
                watcher.Created -= OnAssetChanged;
                watcher.Deleted -= OnAssetChanged;
                watcher.Renamed -= OnAssetRenamed;
                watcher.Dispose();
            }

            _watchers.Clear();
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }
}
