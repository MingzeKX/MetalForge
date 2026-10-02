using Avalonia;
using Avalonia.Media;
using MetalForge.Core.Theming;

namespace MetalForge.App.Services;

/// <summary>
/// 把 Core 层的 <see cref="ThemeDefinition"/> 翻译成 Avalonia 资源。
///
/// 这是 UI 层唯一允许"认识主题"的地方：视图与 ViewModel 只引用资源键，
/// 不得出现颜色或尺寸字面量（由 CI 中的硬编码检查测试强制）。
/// </summary>
public sealed class ThemeResourceService
{
    /// <summary>调色板键前缀，例如 <c>MfBackground</c>。</summary>
    public const string BrushPrefix = "Mf";

    /// <summary>度量键前缀，例如 <c>MfCornerRadius</c>。</summary>
    public const string MetricPrefix = "Mf";

    private Application? _application;

    /// <summary>当前生效的主题。</summary>
    public ThemeDefinition Current { get; private set; } = new();

    /// <summary>应用主题到给定 Application。重复调用会覆盖旧资源。</summary>
    public void Apply(Application application, ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(theme);

        _application = application;
        Current = theme;

        var resources = application.Resources;

        foreach (var key in ThemePalette.Keys)
        {
            var value = theme.Palette[key];
            if (value is null)
            {
                // 主题文件没给这个键。Schema 已保证键存在，走到这里属于配置被人为删空，
                // 记一个明确的资源值以便在界面上看得出来，而不是让 XAML 静默取不到值。
                resources[BrushPrefix + key] = new SolidColorBrush(Colors.Transparent);
                continue;
            }

            resources[BrushPrefix + key] = CreateBrush(value);
        }

        // 度量：数字与 Thickness/CornerRadius 都要暴露，XAML 才能直接用
        var metrics = theme.Metrics;
        resources[MetricPrefix + nameof(ThemeMetrics.CornerRadius)] = new CornerRadius(metrics.CornerRadius);
        resources[MetricPrefix + nameof(ThemeMetrics.CornerRadiusSmall)] = new CornerRadius(metrics.CornerRadiusSmall);
        resources[MetricPrefix + nameof(ThemeMetrics.SpacingUnit)] = metrics.SpacingUnit;
        resources[MetricPrefix + nameof(ThemeMetrics.FontSizeBase)] = metrics.FontSizeBase;
        resources[MetricPrefix + nameof(ThemeMetrics.FontSizeSmall)] = metrics.FontSizeSmall;
        resources[MetricPrefix + nameof(ThemeMetrics.FontSizeHeading)] = metrics.FontSizeHeading;
        resources[MetricPrefix + nameof(ThemeMetrics.PanelPadding)] = new Thickness(metrics.PanelPadding);
        resources[MetricPrefix + nameof(ThemeMetrics.ToolbarHeight)] = metrics.ToolbarHeight;
        resources[MetricPrefix + nameof(ThemeMetrics.StatusBarHeight)] = metrics.StatusBarHeight;
        resources[MetricPrefix + nameof(ThemeMetrics.TabHeight)] = metrics.TabHeight;

        // 编辑器与终端字体族
        resources["MfEditorFontFamily"] = FontFamily.Parse(theme.Editor.FontFamily);
        resources["MfEditorFontSize"] = theme.Editor.FontSize;
        resources["MfTerminalFontFamily"] = FontFamily.Parse(theme.Terminal.FontFamily);
        resources["MfTerminalFontSize"] = theme.Terminal.FontSize;

        // 密度换算出的常用间距（整数倍 spacingUnit），避免 XAML 里写 4/8/12/16
        resources["MfSpace1"] = new Thickness(metrics.Space(1));
        resources["MfSpace2"] = new Thickness(metrics.Space(2));
        resources["MfSpace3"] = new Thickness(metrics.Space(3));
        resources["MfSpace4"] = new Thickness(metrics.Space(4));

        application.RequestedThemeVariant = theme.Variant switch
        {
            "light" => Avalonia.Styling.ThemeVariant.Light,
            "high-contrast" => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Dark,
        };
    }

    /// <summary>构造资源键名，供 XAML 与测试共用，避免字符串漂移。</summary>
    public static string BrushKey(string paletteKey) => BrushPrefix + paletteKey;

    public static string MetricKey(string metricName) => MetricPrefix + metricName;

    private static SolidColorBrush CreateBrush(string hexValue)
    {
        if (!HexColor.TryParse(hexValue, out var color))
        {
            // 主题已经过 Schema 校验；走到这里说明 Schema 有漏网之鱼。
            // 不抛异常（一个坏颜色不该让 UI 崩掉），退化为透明并让调用方可见。
            return new SolidColorBrush(Colors.Transparent);
        }

        return new SolidColorBrush(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
    }

    /// <summary>当前 Application；未应用主题时为 null。</summary>
    public Application? Application => _application;
}
