using CommunityToolkit.Mvvm.ComponentModel;
using MetalForge.Core.Configuration;
using MetalForge.Core.Localization;

namespace MetalForge.App.ViewModels;

/// <summary>
/// 欢迎页 ViewModel。显示品牌信息、接下来能做什么、以及配置健康状态，
/// 让用户（尤其是初学者）第一眼就知道"环境是否就绪、缺什么、下一步点哪"。
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;

    [ObservableProperty]
    private string _headline = "MetalForge";

    [ObservableProperty]
    private string _tagline = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _versionText = string.Empty;

    [ObservableProperty]
    private string _environmentSummary = string.Empty;

    [ObservableProperty]
    private string _configurationSummary = string.Empty;

    [ObservableProperty]
    private string _nextStepsTitle = string.Empty;

    [ObservableProperty]
    private string _configHint = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> _nextSteps = [];

    [ObservableProperty]
    private bool _hasConfigurationProblems;

    public WelcomeViewModel(ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        _localization = localization;
    }

    /// <summary>用当前配置刷新全部显示字段。</summary>
    public void UpdateFrom(MetalForgeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var branding = configuration.Branding;
        Headline = branding.Name;
        Tagline = branding.Tagline ?? string.Empty;
        Description = branding.Description ?? string.Empty;
        VersionText = $"{_localization["settings.title"]}: {branding.Version}";
        EnvironmentSummary = $"{RuntimeDescription()} · {PlatformDescription()}";

        NextStepsTitle = _localization["welcome.nextSteps"];
        NextSteps =
        [
            _localization["welcome.stepNewProject"],
            _localization["welcome.stepOpenProject"],
            _localization["welcome.stepToolchain"],
            _localization["welcome.stepDocs"],
        ];

        ConfigHint = _localization["welcome.configHint"];

        var problems = configuration.Diagnostics
            .Where(diagnostic => diagnostic.Severity >= Core.Diagnostics.DiagnosticSeverity.Warning)
            .ToArray();

        HasConfigurationProblems = problems.Length > 0;
        ConfigurationSummary = problems.Length == 0
            ? _localization.Format("welcome.configOk", configuration.AvailableThemes.Count)
            : _localization.Format("welcome.configProblems", problems.Length, problems[0].Message);
    }

    private static string RuntimeDescription()
        => $".NET {Environment.Version.ToString(3)}";

    private static string PlatformDescription()
    {
        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        return $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription.Split('-')[0].Trim()} ({architecture})";
    }
}
