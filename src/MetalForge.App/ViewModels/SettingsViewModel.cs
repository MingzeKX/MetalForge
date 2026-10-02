using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetalForge.Core.Configuration;
using MetalForge.Core.Localization;

namespace MetalForge.App.ViewModels;

/// <summary>
/// 设置页（M1 只覆盖"由配置驱动、且现在就能生效"的项：主题、语言、布局）。
///
/// 刻意不把尚未实现的设置项列出来：一个点了没反应的开关比没有这个开关更糟。
/// 其余分组（编辑器、构建、运行、快捷键、工具链、AI）在对应里程碑加入。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigurationService _configuration;
    private readonly ILocalizationService _localization;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _appearanceTitle = string.Empty;

    [ObservableProperty]
    private string _themeLabel = string.Empty;

    [ObservableProperty]
    private string _languageLabel = string.Empty;

    [ObservableProperty]
    private string _densityLabel = string.Empty;

    [ObservableProperty]
    private string _densityValue = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<Core.Theming.ThemeDefinition> _themes = [];

    [ObservableProperty]
    private IReadOnlyList<LanguageOption> _languages = [];

    [ObservableProperty]
    private Core.Theming.ThemeDefinition? _selectedTheme;

    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    public SettingsViewModel(IConfigurationService configuration, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(localization);

        _configuration = configuration;
        _localization = localization;
    }

    /// <summary>语言下拉项（LanguageCode + 显示名）。</summary>
    public sealed record LanguageOption(string LanguageCode, string DisplayName);

    /// <summary>用当前配置刷新可选项。</summary>
    public void Refresh()
    {
        Title = _localization["settings.title"];
        AppearanceTitle = _localization["settings.appearance"];
        ThemeLabel = _localization["settings.theme"];
        LanguageLabel = _localization["settings.language"];
        DensityLabel = _localization["settings.density"];
        DensityValue = _configuration.CurrentTheme.Metrics.Density;

        Themes = _configuration.AvailableThemes;
        Languages = [.. _localization.AvailableLanguages.Select(language => new LanguageOption(language.LanguageCode, language.DisplayName))];

        // 用生成的属性赋值（直接写字段会被 MVVMTK0034 拦下：
        // 那样会绕过变更通知，界面不会更新）。
        SelectedTheme = _configuration.CurrentTheme;
        SelectedLanguage = Languages.FirstOrDefault(
            language => string.Equals(language.LanguageCode, _localization.CurrentLanguage, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>用户选择主题。</summary>
    [RelayCommand]
    public void SelectTheme(Core.Theming.ThemeDefinition? theme)
    {
        if (theme is not null)
        {
            _configuration.TryApplyTheme(theme.Id);
        }
    }

    /// <summary>用户选择语言。</summary>
    [RelayCommand]
    public void SelectLanguage(LanguageOption? language)
    {
        if (language is not null)
        {
            _localization.TrySetLanguage(language.LanguageCode);
        }
    }
}
