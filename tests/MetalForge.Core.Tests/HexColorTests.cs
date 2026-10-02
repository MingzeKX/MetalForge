using MetalForge.Core.Theming;

namespace MetalForge.Core.Tests;

public sealed class HexColorTests
{
    [Theory]
    [InlineData("#7AA2C8", 255, 0x7A, 0xA2, 0xC8)]
    [InlineData("7AA2C8", 255, 0x7A, 0xA2, 0xC8)]
    [InlineData("#7aa2c8", 255, 0x7A, 0xA2, 0xC8)]
    [InlineData("  #7AA2C8  ", 255, 0x7A, 0xA2, 0xC8)]
    [InlineData("#807AA2C8", 0x80, 0x7A, 0xA2, 0xC8)]
    [InlineData("#00000000", 0, 0, 0, 0)]
    [InlineData("#FF000000", 255, 0, 0, 0)]
    public void TryParse_AcceptsValidForms(string text, int alpha, int red, int green, int blue)
    {
        var parsed = HexColor.TryParse(text, out var color);

        Assert.True(parsed, $"应当能解析 '{text}'");
        Assert.Equal((byte)alpha, color.Alpha);
        Assert.Equal((byte)red, color.Red);
        Assert.Equal((byte)green, color.Green);
        Assert.Equal((byte)blue, color.Blue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#123456789")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    [InlineData("rgb(1,2,3)")]
    public void TryParse_RejectsInvalidForms(string? text)
    {
        Assert.False(HexColor.TryParse(text, out _), $"不应接受 '{text}'");
    }

    [Fact]
    public void ToString_RoundTripsOpaqueColors()
    {
        var color = HexColor.Parse("#7AA2C8");

        Assert.Equal("#7AA2C8", color.ToString());
    }

    [Fact]
    public void ToString_RoundTripsTranslucentColors()
    {
        var color = HexColor.Parse("#807AA2C8");

        Assert.Equal("#807AA2C8", color.ToString());
    }

    [Fact]
    public void Parse_ThrowsWithActionableMessage()
    {
        var exception = Assert.Throws<FormatException>(() => HexColor.Parse("nope"));

        Assert.Contains("nope", exception.Message, StringComparison.Ordinal);
        Assert.Contains("#RRGGBB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultThemePalette_MissingUntilResolved()
    {
        // 颜色不写死在 Core 里：未经 JSON 解析的调色板应当整体为 null，
        // 这样"assets/themes/*.theme.json 是唯一配色真相源"才是真的。
        var palette = new ThemePalette();

        Assert.Equal(ThemePalette.Keys.Count, palette.MissingKeys().Count);
        foreach (var key in ThemePalette.Keys)
        {
            Assert.Null(palette[key]);
        }
    }

    [Fact]
    public void OverlayWith_KeepsParentColorsAndAppliesOverrides()
    {
        var parent = new ThemePalette().WithColor(nameof(ThemePalette.Accent), "#111111");
        var overlay = new ThemePalette().WithColor(nameof(ThemePalette.Border), "#222222");

        var merged = parent.OverlayWith(overlay);

        Assert.Equal("#111111", merged.Accent);
        Assert.Equal("#222222", merged.Border);
        Assert.Null(merged.Background);
    }

    [Fact]
    public void ThemePaletteIndexer_ReturnsNullForUnknownKey()
    {
        Assert.Null(new ThemePalette()["not-a-key"]);
    }
}
