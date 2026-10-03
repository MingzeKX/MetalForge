using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetalForge.Core.Toolchains;

namespace MetalForge.App.ViewModels;

/// <summary>工具链健康面板里的一个工具行。</summary>
public sealed partial class ToolRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _versionText = string.Empty;

    [ObservableProperty]
    private string _originText = string.Empty;

    [ObservableProperty]
    private string _detailText = string.Empty;

    /// <summary>状态对应的语义色键（success/warning/danger/foregroundMuted）。</summary>
    public string StatusBrushKey { get; init; } = "MfForegroundMuted";

    public required string ToolId { get; init; }

    public required string DisplayName { get; init; }

    public required string Purpose { get; init; }

    public bool IsReady => StatusBrushKey == "MfSuccess";

    public string? DownloadUrl { get; init; }

    /// <summary>是否提供"打开获取页面"的动作。</summary>
    public bool CanOpenDownload => !string.IsNullOrWhiteSpace(DownloadUrl);
}

/// <summary>按类别分组的一组工具。</summary>
public sealed partial class ToolGroupViewModel : ObservableObject
{
    public required string CategoryKey { get; init; }

    public required string CategoryTitle { get; init; }

    public IReadOnlyList<ToolRowViewModel> Tools { get; init; } = [];
}

/// <summary>语法高亮的一条自检结果。</summary>
public sealed partial class HighlightStatusViewModel : ObservableObject
{
    public required string LanguageName { get; init; }

    /// <summary>自检命中的高亮区段数；0 表示该语言的规则实际没有生效。</summary>
    public required int MatchedSections { get; init; }

    public required string StatusText { get; init; }

    /// <summary>0 区段用警示色：那说明高亮写错了，而不是"没有语法"。</summary>
    public string StatusBrushKey => MatchedSections > 0 ? "MfSuccess" : "MfWarning";
}

/// <summary>
/// 工具链健康面板（G-01 的界面部分）。
///
/// 它存在的理由：本机实测缺少全部 OSDev 工具链，新手若不被告知"缺什么、怎么装"，
/// 会在第一步就卡死。此面板把探测结果翻译成可执行的下一步。
/// </summary>
public sealed partial class ToolchainHealthViewModel : ObservableObject
{
    private readonly IToolLocator _locator;
    private readonly Func<string, string> _localize;
    private readonly Func<string, object?[], string> _format;

    [ObservableProperty]
    private string _headline = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _lastCheckedText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private IReadOnlyList<ToolGroupViewModel> _groups = [];

    [ObservableProperty]
    private IReadOnlyList<ToolRowViewModel> _missingTools = [];

    [ObservableProperty]
    private bool _hasMissingTools;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>缺失工具区块的标题（本地化）。</summary>
    [ObservableProperty]
    private string _missingSectionTitle = string.Empty;

    /// <summary>面板底部的解释文字（本地化）。</summary>
    [ObservableProperty]
    private string _explainTitle = string.Empty;

    [ObservableProperty]
    private string _explainBody = string.Empty;

    /// <summary>
    /// 语法高亮自检结果。与工具链无关，但同属"当前环境是否正常"，
    /// 放在同一面板可以让用户在一个地方确认"IDE 自身能力"是否可用。
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<HighlightStatusViewModel> _highlightStatuses = [];

    [ObservableProperty]
    private string _highlightTitle = string.Empty;

    [ObservableProperty]
    private string _highlightSummary = string.Empty;

    public ToolchainHealthViewModel(
        IToolLocator locator,
        Func<string, string> localize,
        Func<string, object?[], string> format)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(localize);
        ArgumentNullException.ThrowIfNull(format);

        _locator = locator;
        _localize = localize;
        _format = format;
    }

    /// <summary>
    /// 填入语法高亮自检结果（由宿主在探测器就绪后调用）。
    /// 不在构造函数里做：探测要读文件与加载定义，属于启动路径外的工作。
    /// </summary>
    public void SetHighlightStatuses(IReadOnlyDictionary<string, int> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        HighlightTitle = _localize("toolchain.highlightTitle");
        HighlightStatuses =
        [
            .. results
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new HighlightStatusViewModel
                {
                    LanguageName = pair.Key,
                    MatchedSections = pair.Value,
                    StatusText = pair.Value > 0
                        ? _format("toolchain.highlightOk", [pair.Value])
                        : _localize("toolchain.highlightBroken"),
                }),
        ];

        var working = results.Count(pair => pair.Value > 0);
        HighlightSummary = _format("toolchain.highlightSummary", [working, results.Count]);
    }

    /// <summary>执行探测并刷新界面。</summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            _locator.Invalidate();
            var report = await _locator.CheckHealthAsync(cancellationToken).ConfigureAwait(true);
            Apply(report);
        }
        catch (OperationCanceledException)
        {
            // 用户切换面板导致取消：保持现状即可，不当作错误。
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 探测本身已尽量把失败降级为诊断；这里是最后一道防线。
            ErrorMessage = exception.Message;
            Summary = _localize("toolchain.statusMissing");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(ToolHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Headline = _localize("toolchain.title");
        Summary = report.Describe();
        LastCheckedText = _format("toolchain.lastChecked", [report.CheckedAt.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)]);
        MissingSectionTitle = _localize("toolchain.missingSectionTitle");
        ExplainTitle = _localize("toolchain.explainTitle");
        ExplainBody = _localize("toolchain.explainBody");

        Groups =
        [
            .. report.ByCategory().Select(group => new ToolGroupViewModel
            {
                CategoryKey = group.Key,
                CategoryTitle = CategoryTitle(group.Key),
                Tools = [.. group.Select(CreateRow)],
            }),
        ];

        var missing = report.MissingRequiredTools().Select(CreateRow).ToArray();
        MissingTools = missing;
        HasMissingTools = missing.Length > 0;
    }

    private ToolRowViewModel CreateRow(ToolHealth health)
    {
        var requirement = health.Requirement;

        var (statusKey, brushKey) = health.Status switch
        {
            ToolStatus.Ready => ("toolchain.found", "MfSuccess"),
            ToolStatus.Outdated => ("toolchain.outdated", "MfWarning"),
            ToolStatus.Broken => ("toolchain.outdated", "MfWarning"),
            _ => ("toolchain.missing", "MfDanger"),
        };

        var versionText = health.Instance?.Version?.ToString() ?? "—";
        var originText = health.Instance is null ? "—" : SourceText(health.Instance.Source);

        var detail = health.Status == ToolStatus.Missing
            ? health.StatusDetail ?? string.Empty
            : health.StatusDetail ?? string.Empty;

        return new ToolRowViewModel
        {
            ToolId = requirement.ToolId,
            DisplayName = requirement.DisplayName,
            Purpose = requirement.Purpose,
            StatusText = _localize(statusKey),
            StatusBrushKey = brushKey,
            VersionText = versionText,
            OriginText = originText,
            DetailText = detail,
            DownloadUrl = requirement.DownloadUrl,
        };
    }

    private string SourceText(ToolSource source) => _localize(source switch
    {
        ToolSource.ProjectConfiguration => "toolchain.sourceProjectConfig",
        ToolSource.UserConfiguration => "toolchain.sourceUserConfig",
        ToolSource.ManagedDirectory => "toolchain.sourceManaged",
        ToolSource.SystemPath => "toolchain.sourcePath",
        ToolSource.KnownLocation => "toolchain.sourceKnownLocation",
        _ => "toolchain.missing",
    });

    private string CategoryTitle(string category) => category switch
    {
        "build" => _localize("toolchain.categoryBuild"),
        "toolchain" => _localize("toolchain.categoryToolchain"),
        "emulator" => _localize("toolchain.categoryEmulator"),
        "debugger" => _localize("toolchain.categoryDebugger"),
        "flash" => _localize("toolchain.categoryFlash"),
        "language" => _localize("toolchain.categoryLanguage"),
        _ => _localize("toolchain.categoryGeneral"),
    };
}
