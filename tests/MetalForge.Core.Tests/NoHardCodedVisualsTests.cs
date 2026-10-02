using System.Text.RegularExpressions;

namespace MetalForge.Core.Tests;

/// <summary>
/// 把"不硬编码"从口头约束变成会失败的测试。
/// 覆盖 DESIGN.md 第七节（元信息配置化）与第十四节（国际化）的核心要求。
/// </summary>
public sealed class NoHardCodedVisualsTests
{
    /// <summary>颜色字面量：<c>#RRGGBB</c>、<c>#AARRGGBB</c>，以及常见命名色。</summary>
    private static readonly Regex ColorLiteral = new(
        """#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})\b""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>C# 中的已知颜色常量，例如 <c>Colors.Red</c>。</summary>
    private static readonly Regex NamedColor = new(
        """\bColors\.[A-Z][A-Za-z]+\b""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 这些文件天然包含颜色/尺寸：主题翻译层与生成脚本。
    /// 白名单必须保持最小，新增项要有理由。
    /// </summary>
    private static readonly string[] AllowedRelativePaths =
    [
        // 主题翻译层：Core 的颜色值在 Schema 校验后到这里变成画刷，是唯一的转换边界。
        @"src\MetalForge.App\Services\ThemeResourceService.cs",
    ];

    [Fact]
    public void AppAndCore_MustNotContainColorLiterals()
    {
        var offenders = new List<string>();

        foreach (var file in EnumerateSourceFiles())
        {
            if (IsAllowed(file))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(TestPaths.RepositoryRoot, file);

            foreach (Match match in ColorLiteral.Matches(text))
            {
                offenders.Add($"{relative}: 颜色字面量 '{match.Value}'");
            }

            foreach (Match match in NamedColor.Matches(text))
            {
                offenders.Add($"{relative}: 命名颜色 '{match.Value}'");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "视觉参数必须来自 assets/themes/*.theme.json，不得硬编码：\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void PaletteResources_CoverEveryThemePaletteKey()
    {
        // 主题调色板的每个键都必须有对应的资源键命名规则，否则 XAML 会静默取不到值。
        var keys = MetalForge.Core.Theming.ThemePalette.Keys;
        Assert.NotEmpty(keys);

        foreach (var key in keys)
        {
            var resourceKey = MetalForge.App.Services.ThemeResourceService.BrushKey(key);
            Assert.StartsWith("Mf", resourceKey, StringComparison.Ordinal);
            Assert.EndsWith(key, resourceKey, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MainWindowXaml_ReferencesThemeResourcesOnly()
    {
        var xamlPath = Path.Combine(TestPaths.RepositoryRoot, "src", "MetalForge.App", "Views", "MainWindow.axaml");
        Assert.True(File.Exists(xamlPath), $"找不到 {xamlPath}");

        var xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotMatch(ColorLiteral, xaml);
        Assert.Contains("DynamicResource Mf", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void BrandingJson_IsTheOnlySourceOfAppNameInViews()
    {
        // 视图里不得出现品牌名硬编码：欢迎页标题必须来自绑定或配置。
        // 只检查"会显示给用户的文本属性"，命名空间与 x:Class 里的程序集名不算硬编码。
        var viewsDirectory = Path.Combine(TestPaths.RepositoryRoot, "src", "MetalForge.App", "Views");
        Assert.True(Directory.Exists(viewsDirectory));

        var textAttribute = new Regex(
            "(?:Text|Title|Content|Header|Watermark)\\s*=\\s*\"([^\"]*)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(viewsDirectory, "*.axaml"))
        {
            var xaml = File.ReadAllText(file);
            var relative = Path.GetRelativePath(TestPaths.RepositoryRoot, file);

            foreach (Match match in textAttribute.Matches(xaml))
            {
                var value = match.Groups[1].Value;

                // Title="MetalForge" 是设计器占位，运行时会被 branding.FormatWindowTitle() 覆盖。
                if (string.Equals(value, "MetalForge", StringComparison.Ordinal)
                    && match.Value.StartsWith("Title=", StringComparison.Ordinal))
                {
                    continue;
                }

                if (value.Contains("MetalForge", StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}: {match.Value}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "品牌名必须来自 assets/branding/app.json（绑定或配置），不得写进视图文本：\n  " + string.Join("\n  ", offenders));
    }

    private static IEnumerable<string> EnumerateSourceFiles()
    {
        foreach (var project in new[] { "MetalForge.Core", "MetalForge.App", "MetalForge.Templates" })
        {
            var directory = Path.Combine(TestPaths.RepositoryRoot, "src", project);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var extension = Path.GetExtension(file);
                if (extension is ".cs" or ".axaml" or ".xaml" or ".json")
                {
                    yield return file;
                }
            }
        }
    }

    private static bool IsAllowed(string file)
    {
        var relative = Path.GetRelativePath(TestPaths.RepositoryRoot, file);
        return AllowedRelativePaths.Any(allowed => string.Equals(allowed, relative, StringComparison.OrdinalIgnoreCase));
    }
}
