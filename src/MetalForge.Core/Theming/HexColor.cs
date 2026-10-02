using System.Globalization;

namespace MetalForge.Core.Theming;

/// <summary>
/// 主题颜色值的规范化表示。
/// Core 层不允许依赖任何 UI 类型，因此这里只保存通道值，
/// 由 App 层转换为 Avalonia 的 Color/IBrush。
/// </summary>
public readonly record struct HexColor(byte Alpha, byte Red, byte Green, byte Blue)
{
    /// <summary>解析 <c>#RRGGBB</c> 或 <c>#AARRGGBB</c>（<c>#</c> 可省略，大小写不敏感）。</summary>
    public static bool TryParse(string? text, out HexColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.Length is not (6 or 8))
        {
            return false;
        }

        foreach (var character in span)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        var alpha = span.Length == 8 ? ParseByte(span[..2]) : (byte)0xFF;
        var offset = span.Length == 8 ? 2 : 0;
        var red = ParseByte(span.Slice(offset, 2));
        var green = ParseByte(span.Slice(offset + 2, 2));
        var blue = ParseByte(span.Slice(offset + 4, 2));

        color = new HexColor(alpha, red, green, blue);
        return true;
    }

    /// <summary>解析失败时抛出 <see cref="FormatException"/>。调用方在配置层应优先使用 <see cref="TryParse"/>。</summary>
    public static HexColor Parse(string text)
        => TryParse(text, out var color)
            ? color
            : throw new FormatException($"不是合法的颜色值：'{text}'。期望 #RRGGBB 或 #AARRGGBB。");

    /// <summary>输出为 <c>#RRGGBB</c>；当 alpha 不为 255 时输出 <c>#AARRGGBB</c>。</summary>
    public override string ToString()
        => Alpha == 0xFF
            ? $"#{Red:X2}{Green:X2}{Blue:X2}"
            : $"#{Alpha:X2}{Red:X2}{Green:X2}{Blue:X2}";

    private static byte ParseByte(ReadOnlySpan<char> hex)
        => byte.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
