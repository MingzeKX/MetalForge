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

/// <summary>
/// 把"脚本必须纯 ASCII"从纪律变成会失败的测试。
///
/// 原因：Windows PowerShell 5.1 用系统 ANSI 代码页（中文机器上是 GBK）解码没有 BOM 的 .ps1。
/// 含非 ASCII 的脚本会变成乱码并解析失败——本项目已经因此两次踩坑，其中一次是
/// 在一份明确写了这条纪律的 ADR 之后又犯的。凡靠"记得"维持的约定必然失效。
///
/// 需要非 ASCII 输出的脚本必须经 scripts/Invoke-RepoScript.ps1 在 pwsh 下运行，
/// 或把二进制资源放到 .json / .md 里由脚本读取。
/// </summary>
public sealed class ScriptEncodingTests
{
    [Fact]
    public void AllPowerShellScripts_MustBeAsciiOnly()
    {
        var scriptsDirectory = Path.Combine(TestPaths.RepositoryRoot, "scripts");
        Assert.True(Directory.Exists(scriptsDirectory), $"找不到脚本目录：{scriptsDirectory}");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(scriptsDirectory, "*.ps1", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            var nonAsciiCount = bytes.Count(b => b > 127);

            if (nonAsciiCount > 0)
            {
                var relative = Path.GetRelativePath(TestPaths.RepositoryRoot, file);
                var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                offenders.Add($"{relative}（{nonAsciiCount} 个非 ASCII 字节，BOM={hasBom}）");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "PowerShell 脚本必须保持纯 ASCII，否则 Windows PowerShell 5.1 会按 ANSI 解码导致解析失败。\n"
            + "  需要非 ASCII 内容时：放到 .json/.md 资源里，或经 scripts/Invoke-RepoScript.ps1 用 pwsh 运行。\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void AllMarkdownAndJsonAssets_AreValidUtf8()
    {
        // 反向约束：资源与文档必须是合法 UTF-8（中文正常显示的前提）。
        var strictUtf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var failures = new List<string>();

        foreach (var root in new[] { "assets", "docs" })
        {
            var directory = Path.Combine(TestPaths.RepositoryRoot, root);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            {
                if (Path.GetExtension(file) is not (".json" or ".md"))
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(file);
                try
                {
                    _ = strictUtf8.GetString(bytes);
                }
                catch (ArgumentException exception)
                {
                    failures.Add($"{Path.GetRelativePath(TestPaths.RepositoryRoot, file)}: {exception.Message}");
                }
            }
        }

        Assert.True(failures.Count == 0, "以下文件不是合法 UTF-8：\n  " + string.Join("\n  ", failures));
    }
}
