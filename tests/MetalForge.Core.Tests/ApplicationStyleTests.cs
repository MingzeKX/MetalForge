namespace MetalForge.Core.Tests;

/// <summary>
/// 应用样式装配的回归测试。
///
/// 为什么需要：<c>TextEditor</c>（AvaloniaEdit）缺少 <c>StyleInclude</c> 时的症状
/// 极具误导性 —— 控件参与布局、有正常尺寸、<c>IsVisible</c> 为 true、文档与高亮都已就绪，
/// 但**一个像素都不绘制**，屏幕上是一片纯背景色。所有日志都显示一切正常，
/// 因此这类缺陷只能靠"检查装配清单"来防。
/// </summary>
public sealed class ApplicationStyleTests
{
    private static string ReadAppManifest() =>
        File.ReadAllText(Path.Combine(TestPaths.AssetsDirectory, "..", "src", "MetalForge.App", "App.axaml"));

    [Fact]
    public void AppManifest_IncludesAvaloniaEditTheme()
    {
        var manifest = ReadAppManifest();

        Assert.Contains(
            "avares://AvaloniaEdit/Themes/",
            manifest,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppManifest_IncludesOwnStylesAfterTheme()
    {
        // 顺序有含义：自有样式必须排在外来主题之后，否则我们的选择器优先级会被覆盖。
        var manifest = ReadAppManifest();

        var editThemeIndex = manifest.IndexOf("avares://AvaloniaEdit/Themes/", StringComparison.Ordinal);
        var ownStylesIndex = manifest.IndexOf("avares://MetalForge/Styles.axaml", StringComparison.Ordinal);
        var fluentIndex = manifest.IndexOf("<FluentTheme", StringComparison.Ordinal);

        Assert.True(fluentIndex >= 0, "缺少 FluentTheme");
        Assert.True(ownStylesIndex > fluentIndex, "自有样式应在 FluentTheme 之后");
        Assert.True(editThemeIndex > fluentIndex, "AvaloniaEdit 主题应在 FluentTheme 之后");
        Assert.True(ownStylesIndex > editThemeIndex, "自有样式应在 AvaloniaEdit 主题之后");
    }

    [Fact]
    public void AppManifest_DeclaresNoLiteralVisualValues()
    {
        // 项目纪律：视觉参数全部来自主题 JSON，清单里不应出现颜色或字号字面量。
        var manifest = ReadAppManifest();

        Assert.DoesNotContain("#", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("FontSize=", manifest, StringComparison.Ordinal);
    }
}
