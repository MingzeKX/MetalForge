using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using MetalForge.Core.Configuration;

namespace MetalForge.App.ViewModels;

/// <summary>
/// 欢迎页 ViewModel。显示品牌信息与配置健康状态，
/// 让用户（尤其是初学者）第一眼就知道"环境是否就绪、缺什么"。
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
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
    private bool _hasConfigurationProblems;

    /// <summary>用当前配置刷新全部显示字段。</summary>
    public void UpdateFrom(MetalForgeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var branding = configuration.Branding;
        Headline = branding.Name;
        Tagline = branding.Tagline ?? string.Empty;
        Description = branding.Description ?? string.Empty;
        VersionText = $"版本 {branding.Version}";
        EnvironmentSummary = $"{RuntimeDescription()} · {PlatformDescription()}";

        var problems = configuration.Diagnostics
            .Where(diagnostic => diagnostic.Severity >= Core.Diagnostics.DiagnosticSeverity.Warning)
            .ToArray();

        HasConfigurationProblems = problems.Length > 0;
        ConfigurationSummary = problems.Length == 0
            ? $"配置已加载：{configuration.AvailableThemes.Count} 套主题，全部通过 Schema 校验。"
            : $"配置有 {problems.Length} 处需要注意：{problems[0].Message}";
    }

    private static string RuntimeDescription()
        => $".NET {Environment.Version.ToString(3)}";

    private static string PlatformDescription()
    {
        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        return $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription.Split('-')[0].Trim()} ({architecture})";
    }
}
