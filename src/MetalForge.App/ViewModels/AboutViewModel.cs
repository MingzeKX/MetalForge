using CommunityToolkit.Mvvm.ComponentModel;
using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;
using MetalForge.Core.Localization;

namespace MetalForge.App.ViewModels;

/// <summary>
/// "关于"标签页。内容全部来自 assets/branding/about.json 与 app.json，
/// 以及构建时注入的版本信息——代码里不写任何品牌文字。
/// </summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _headline = string.Empty;

    [ObservableProperty]
    private string _subheadline = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _versionText = string.Empty;

    [ObservableProperty]
    private string _runtimeText = string.Empty;

    [ObservableProperty]
    private string _platformText = string.Empty;

    [ObservableProperty]
    private string _copyright = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<AboutCredit> _credits = [];

    [ObservableProperty]
    private IReadOnlyList<AboutSection> _sections = [];

    [ObservableProperty]
    private IReadOnlyList<BrandingLink> _links = [];

    [ObservableProperty]
    private string _creditsTitle = string.Empty;

    [ObservableProperty]
    private string _linksTitle = string.Empty;

    [ObservableProperty]
    private string _diagnosticSummaryText = string.Empty;

    public AboutViewModel(ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        _localization = localization;
    }

    public void UpdateFrom(MetalForgeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var branding = configuration.Branding;
        var about = configuration.About;

        Title = about.Title;
        Headline = about.Headline ?? branding.Name;
        Subheadline = about.Subheadline ?? branding.Tagline ?? string.Empty;
        Description = about.Description ?? branding.Description ?? string.Empty;
        Copyright = branding.Copyright ?? string.Empty;

        VersionText = about.BuildInfo.ShowRuntime
            ? $"v{branding.Version}"
            : string.Empty;

        RuntimeText = about.BuildInfo.ShowRuntime ? $".NET {Environment.Version.ToString(3)}" : string.Empty;
        PlatformText = about.BuildInfo.ShowPlatform
            ? System.Runtime.InteropServices.RuntimeInformation.OSDescription
            : string.Empty;

        Credits = about.Credits;
        Sections = about.Sections;
        Links = branding.Links;

        CreditsTitle = _localization["about.creditsTitle"];
        LinksTitle = _localization["about.linksTitle"];
        DiagnosticSummaryText = DiagnosticSummary(configuration);
    }

    /// <summary>配置诊断摘要，显示在"关于"页底部，便于用户报告问题时复制。</summary>
    public string DiagnosticSummary(MetalForgeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = configuration.Diagnostics.Count(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Error);
        var warnings = configuration.Diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);

        return errors == 0 && warnings == 0
            ? _localization["about.noDiagnostics"]
            : _localization.Format("status.errorsAndWarnings", errors, warnings);
    }
}
