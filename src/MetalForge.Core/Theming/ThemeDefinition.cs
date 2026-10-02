namespace MetalForge.Core.Theming;

/// <summary>
/// 主题元信息与调色板。字段名与 <c>assets/themes/schema/theme.schema.json</c> 一一对应。
///
/// 值为可空：<c>assets/themes/*.theme.json</c> 是配色方案唯一真相源。
/// 这里刻意不写任何颜色字面量——Core 中硬编码调色板会让"非程序员改 JSON 换配色"
/// 变成一句空话（架构测试 NoHardCodedVisualsTests 会强制这一点）。
/// </summary>
public sealed record ThemePalette
{
    public string? Background { get; init; }
    public string? Surface { get; init; }
    public string? SurfaceRaised { get; init; }
    public string? SurfaceSunken { get; init; }
    public string? Foreground { get; init; }
    public string? ForegroundMuted { get; init; }
    public string? ForegroundDisabled { get; init; }
    public string? Accent { get; init; }
    public string? AccentHover { get; init; }
    public string? AccentPressed { get; init; }
    public string? Border { get; init; }
    public string? BorderStrong { get; init; }
    public string? Selection { get; init; }
    public string? Success { get; init; }
    public string? Warning { get; init; }
    public string? Danger { get; init; }
    public string? Info { get; init; }

    /// <summary>按调色板键名取值，供主题服务与测试统一访问。</summary>
    public string? this[string key] => key switch
    {
        nameof(Background) => Background,
        nameof(Surface) => Surface,
        nameof(SurfaceRaised) => SurfaceRaised,
        nameof(SurfaceSunken) => SurfaceSunken,
        nameof(Foreground) => Foreground,
        nameof(ForegroundMuted) => ForegroundMuted,
        nameof(ForegroundDisabled) => ForegroundDisabled,
        nameof(Accent) => Accent,
        nameof(AccentHover) => AccentHover,
        nameof(AccentPressed) => AccentPressed,
        nameof(Border) => Border,
        nameof(BorderStrong) => BorderStrong,
        nameof(Selection) => Selection,
        nameof(Success) => Success,
        nameof(Warning) => Warning,
        nameof(Danger) => Danger,
        nameof(Info) => Info,
        _ => null,
    };

    /// <summary>按调色板键名设置值，返回新的调色板。</summary>
    public ThemePalette WithColor(string key, string value) => key switch
    {
        nameof(Background) => this with { Background = value },
        nameof(Surface) => this with { Surface = value },
        nameof(SurfaceRaised) => this with { SurfaceRaised = value },
        nameof(SurfaceSunken) => this with { SurfaceSunken = value },
        nameof(Foreground) => this with { Foreground = value },
        nameof(ForegroundMuted) => this with { ForegroundMuted = value },
        nameof(ForegroundDisabled) => this with { ForegroundDisabled = value },
        nameof(Accent) => this with { Accent = value },
        nameof(AccentHover) => this with { AccentHover = value },
        nameof(AccentPressed) => this with { AccentPressed = value },
        nameof(Border) => this with { Border = value },
        nameof(BorderStrong) => this with { BorderStrong = value },
        nameof(Selection) => this with { Selection = value },
        nameof(Success) => this with { Success = value },
        nameof(Warning) => this with { Warning = value },
        nameof(Danger) => this with { Danger = value },
        nameof(Info) => this with { Info = value },
        _ => this,
    };

    /// <summary>用 <paramref name="overlay"/> 中非空的颜色覆盖本调色板。</summary>
    public ThemePalette OverlayWith(ThemePalette overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        var result = this;
        foreach (var key in Keys)
        {
            var value = overlay[key];
            if (value is not null)
            {
                result = result.WithColor(key, value);
            }
        }

        return result;
    }

    /// <summary>缺失（null）的颜色键列表，用于诊断"主题不完整"。</summary>
    public IReadOnlyList<string> MissingKeys() => [.. Keys.Where(key => this[key] is null)];

    /// <summary>全部键名（用于遍历与校验）。</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        nameof(Background), nameof(Surface), nameof(SurfaceRaised), nameof(SurfaceSunken),
        nameof(Foreground), nameof(ForegroundMuted), nameof(ForegroundDisabled),
        nameof(Accent), nameof(AccentHover), nameof(AccentPressed),
        nameof(Border), nameof(BorderStrong), nameof(Selection),
        nameof(Success), nameof(Warning), nameof(Danger), nameof(Info),
    ];
}

/// <summary>版式度量。所有间距与圆角都从这里取，禁止硬编码。</summary>
public sealed record ThemeMetrics
{
    public double CornerRadius { get; init; } = 6;
    public double CornerRadiusSmall { get; init; } = 4;
    public double SpacingUnit { get; init; } = 4;
    public double FontSizeBase { get; init; } = 13;
    public double FontSizeSmall { get; init; } = 12;
    public double FontSizeHeading { get; init; } = 18;
    public string Density { get; init; } = "comfortable";
    public double PanelPadding { get; init; } = 8;
    public double ToolbarHeight { get; init; } = 38;
    public double StatusBarHeight { get; init; } = 24;
    public double TabHeight { get; init; } = 32;

    /// <summary>把 spacingUnit 的整数倍换算为像素，保证全局节奏一致。</summary>
    public double Space(int units) => SpacingUnit * units;
}

/// <summary>编辑器视觉与行为参数。</summary>
public sealed record ThemeEditorOptions
{
    public string FontFamily { get; init; } = "Cascadia Mono, Consolas, monospace";
    public double FontSize { get; init; } = 13;
    public double LineHeight { get; init; } = 1.5;
    public bool ShowLineNumbers { get; init; } = true;
    public bool HighlightCurrentLine { get; init; } = true;
    public bool WordWrap { get; init; }
    public int TabSize { get; init; } = 4;
    public bool ConvertTabsToSpaces { get; init; } = true;
}

/// <summary>终端与串口面板的视觉参数，含 16 色 ANSI 调色板。</summary>
public sealed record ThemeTerminalOptions
{
    public string FontFamily { get; init; } = "Cascadia Mono, Consolas, monospace";
    public double FontSize { get; init; } = 12;

    /// <summary>ANSI 颜色名到 <c>#RRGGBB</c> 的映射；缺项由实现回退到主题调色板。</summary>
    public IReadOnlyDictionary<string, string> AnsiPalette { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>一个完整主题：元信息 + 调色板 + 度量 + 编辑器/终端参数。</summary>
public sealed record ThemeDefinition
{
    public string Id { get; init; } = "metalforge-dark";
    public string DisplayName { get; init; } = "MetalForge Dark";
    public string? Description { get; init; }
    public string Variant { get; init; } = "dark";
    public string? Extends { get; init; }
    public ThemePalette Palette { get; init; } = new();
    public ThemeMetrics Metrics { get; init; } = new();
    public ThemeEditorOptions Editor { get; init; } = new();
    public ThemeTerminalOptions Terminal { get; init; } = new();

    /// <summary>把 <paramref name="child"/> 中"已显式设置"的字段覆盖到本主题之上（用于 <c>extends</c> 派生）。</summary>
    public ThemeDefinition WithOverlay(ThemeDefinition child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return this with
        {
            Id = child.Id,
            DisplayName = child.DisplayName,
            Description = child.Description ?? Description,
            Variant = child.Variant,
            Extends = child.Extends,
            // 子主题中为 null 的颜色键保留父主题的值。
            Palette = Palette.OverlayWith(child.Palette),
            Metrics = child.Metrics,
            Editor = child.Editor,
            Terminal = new ThemeTerminalOptions
            {
                FontFamily = child.Terminal.FontFamily,
                FontSize = child.Terminal.FontSize,
                AnsiPalette = child.Terminal.AnsiPalette.Count > 0 ? child.Terminal.AnsiPalette : Terminal.AnsiPalette,
            },
        };
    }
}
