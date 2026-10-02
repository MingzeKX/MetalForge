using System.Text.Json.Nodes;
using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Tests;

/// <summary>
/// 配置系统的端到端测试：三级覆盖、Schema 校验、错误回退、热重载。
/// 从真实 <c>assets/</c> 目录加载内置层，覆盖层写进临时目录。
/// </summary>
public sealed class ConfigurationServiceTests : IDisposable
{
    private readonly string _userDirectory;
    private readonly string _projectDirectory;

    public ConfigurationServiceTests()
    {
        var stamp = Guid.NewGuid().ToString("N");
        _userDirectory = Path.Combine(Path.GetTempPath(), "metalforge-tests", "user-" + stamp);
        _projectDirectory = Path.Combine(Path.GetTempPath(), "metalforge-tests", "project-" + stamp);
        Directory.CreateDirectory(_userDirectory);
        Directory.CreateDirectory(Path.Combine(_projectDirectory, ".metalforge"));
    }

    private AssetResolver CreateResolver(bool withProjectLayer = false)
        => new(
            new AssetOptions
            {
                BuiltInAssetsDirectory = TestPaths.AssetsDirectory,
                UserDirectory = _userDirectory,
            },
            withProjectLayer ? _projectDirectory : null);

    private void WriteUserAsset(string relativePath, string content)
    {
        var path = Path.Combine(_userDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, System.Text.Encoding.UTF8);
    }

    private void WriteProjectAsset(string relativePath, string content)
    {
        var path = Path.Combine(_projectDirectory, ".metalforge", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, System.Text.Encoding.UTF8);
    }

    [Fact]
    public async Task LoadsBuiltInBrandingAndThemes()
    {
        Assert.True(TestPaths.AssetsAvailable, "测试需要真实的 assets/ 目录");

        using var service = new ConfigurationService(CreateResolver());
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("MetalForge", service.Current.Branding.Name);
        Assert.False(string.IsNullOrWhiteSpace(service.Current.Branding.Tagline));
        Assert.Equal("metalforge-dark", service.CurrentTheme.Id);
        Assert.Equal("dark", service.CurrentTheme.Variant);

        // 内置资源必须能通过自身 Schema 校验，否则说明仓库里的 JSON 写坏了
        var errors = service.Current.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, "内置配置产生了错误诊断：" + string.Join(" | ", errors.Select(d => d.ToString())));
    }

    [Fact]
    public async Task BuiltInThemes_AllHaveUniqueIdsAndParsableColors()
    {
        using var service = new ConfigurationService(CreateResolver());
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        var themes = service.Current.AvailableThemes;
        Assert.True(themes.Count >= 2, "至少应内置深色与浅色两套主题");

        Assert.Equal(themes.Count, themes.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var theme in themes)
        {
            // 解析后的主题必须每个颜色键都有值——缺失由 MissingKeys 暴露，
            // 而不是靠 Core 里的硬编码兜底悄悄补上。
            Assert.Empty(theme.Palette.MissingKeys());

            foreach (var key in MetalForge.Core.Theming.ThemePalette.Keys)
            {
                var value = theme.Palette[key]!;
                Assert.True(
                    MetalForge.Core.Theming.HexColor.TryParse(value, out _),
                    $"主题 {theme.Id} 的 {key} = '{value}' 不是合法颜色");
            }

            Assert.True(theme.Metrics.CornerRadius >= 0);
            Assert.True(theme.Metrics.FontSizeBase > 0);
        }
    }

    [Fact]
    public async Task UserLayerOverridesBuiltInValue()
    {
        // 用户只写一个字段，其余必须保留内置值
        WriteUserAsset("branding/app.json", """{ "name": "MyForge" }""");

        using var service = new ConfigurationService(CreateResolver());
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("MyForge", service.Current.Branding.Name);
        Assert.Equal("MetalForge Project", service.Current.Branding.Publisher);
        Assert.False(string.IsNullOrWhiteSpace(service.Current.Branding.Tagline));
    }

    [Fact]
    public async Task UserLayerCanDisableSplashWithExplicitNull()
    {
        WriteUserAsset("branding/app.json", """{ "splash": null }""");

        using var service = new ConfigurationService(CreateResolver());
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        // null 删除该键 -> 回落内置默认值
        Assert.True(service.Current.Branding.Splash.Enabled);
    }

    [Fact]
    public async Task ProjectLayerWinsOverUserLayer()
    {
        WriteUserAsset("branding/app.json", """{ "name": "FromUser" }""");
        WriteProjectAsset("branding/app.json", """{ "name": "FromProject" }""");

        using var service = new ConfigurationService(CreateResolver(withProjectLayer: true));
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("FromProject", service.Current.Branding.Name);
    }

    [Fact]
    public async Task InvalidJsonIsRejectedAndOtherLayersStillApply()
    {
        // 缺少逗号：语法错误
        WriteUserAsset("branding/app.json", """{ "name": "Broken" "version": "1.0.0" }""");

        using var service = new ConfigurationService(CreateResolver());
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        // 坏文件不影响启动：回落到内置值
        Assert.Equal("MetalForge", service.Current.Branding.Name);

        var syntaxErrors = service.Current.Diagnostics.Where(d => d.Code == "MFCFG143").ToArray();
        Assert.Single(syntaxErrors);
        Assert.NotNull(syntaxErrors[0].Hint);
        Assert.NotNull(syntaxErrors[0].Exception);
    }

    [Fact]
    public async Task SchemaViolationIsReportedWithPointer()
    {
        // 未知字段（拼写错误）应被 Schema 捕获
        WriteUserAsset("branding/app.json", """{ "name": "X", "taglin": "typo" }""");

        using var service = new ConfigurationService(CreateResolver());
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        var unknownField = service.Current.Diagnostics.FirstOrDefault(d => d.Code == "MFCFG103");
        Assert.NotNull(unknownField);
        Assert.Equal("/taglin", unknownField.Location?.Value);
    }

    [Fact]
    public async Task CustomThemeCanBeAddedByDroppingAFile()
    {
        WriteUserAsset(
            "themes/custom.theme.json",
            """
            {
              "id": "solarized-ish",
              "displayName": "Solarized-ish",
              "variant": "dark",
              "palette": { "accent": "#268BD2" },
              "metrics": { "cornerRadius": 2, "spacingUnit": 3, "fontSizeBase": 14 }
            }
            """);

        using var service = new ConfigurationService(CreateResolver());
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        var custom = service.Current.AvailableThemes.SingleOrDefault(t => t.Id == "solarized-ish");
        Assert.NotNull(custom);
        Assert.Equal("#268BD2", custom.Palette.Accent);
        Assert.Equal(2, custom.Metrics.CornerRadius);
    }

    [Fact]
    public async Task ThemeExtends_InheritsParentPalette()
    {
        WriteUserAsset(
            "themes/high-contrast.theme.json",
            """
            {
              "id": "high-contrast",
              "displayName": "高对比度",
              "variant": "high-contrast",
              "extends": "metalforge-dark",
              "palette": { "background": "#000000", "foreground": "#FFFFFF" }
            }
            """);

        using var service = new ConfigurationService(CreateResolver());
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        var derived = service.Current.AvailableThemes.SingleOrDefault(t => t.Id == "high-contrast");
        Assert.NotNull(derived);
        Assert.Equal("#000000", derived.Palette.Background);
        Assert.Equal("#FFFFFF", derived.Palette.Foreground);
        // 未覆盖的字段来自父主题
        Assert.Equal("#7AA2C8", derived.Palette.Accent);
    }

    [Fact]
    public async Task TryApplyTheme_SwitchesCurrentTheme()
    {
        using var service = new ConfigurationService(CreateResolver());
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.True(service.TryApplyTheme("metalforge-light"));
        Assert.Equal("metalforge-light", service.CurrentTheme.Id);
        Assert.Equal("light", service.CurrentTheme.Variant);

        Assert.False(service.TryApplyTheme("does-not-exist"));
        Assert.Equal("metalforge-light", service.CurrentTheme.Id);
    }

    [Fact]
    public async Task ConfigurationChangedEvent_FiresOnReload()
    {
        using var service = new ConfigurationService(CreateResolver());
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        var raised = 0;
        service.ConfigurationChanged += (_, _) => Interlocked.Increment(ref raised);

        WriteUserAsset("branding/app.json", """{ "name": "Renamed" }""");
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, raised);
        Assert.Equal("Renamed", service.Current.Branding.Name);
    }

    [Fact]
    public async Task MissingAssetDirectory_FallsBackInsteadOfThrowing()
    {
        var resolver = new AssetResolver(new AssetOptions
        {
            BuiltInAssetsDirectory = Path.Combine(Path.GetTempPath(), "metalforge-missing-assets"),
            UserDirectory = _userDirectory,
        });

        using var service = new ConfigurationService(resolver);
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        // 不抛异常，退回默认品牌
        Assert.Equal("MetalForge", service.Current.Branding.Name);
        Assert.NotEmpty(service.Current.Diagnostics);
    }

    [Fact]
    public async Task ResolveAssetPath_PrefersHigherLayer()
    {
        using var service = new ConfigurationService(CreateResolver());
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        var builtIn = service.ResolveAssetPath("themes/metalforge-dark.theme.json");
        Assert.NotNull(builtIn);
        Assert.StartsWith(TestPaths.AssetsDirectory, builtIn, StringComparison.OrdinalIgnoreCase);

        WriteUserAsset("themes/metalforge-dark.theme.json", """{ "id": "metalforge-dark", "displayName": "x", "variant": "dark" }""");
        var overlay = service.ResolveAssetPath("themes/metalforge-dark.theme.json");
        Assert.NotNull(overlay);
        Assert.StartsWith(_userDirectory, overlay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAssetTextAsync_ReturnsBrandingJson()
    {
        using var service = new ConfigurationService(CreateResolver());
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        var text = await service.ReadAssetTextAsync("branding/app.json", TestContext.Current.CancellationToken);

        Assert.NotNull(text);
        var node = JsonNode.Parse(text);
        Assert.Equal("MetalForge", node!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Enumerate_MergesThemeFilesFromAllLayers()
    {
        WriteUserAsset("themes/extra.theme.json", """{ "id": "extra", "displayName": "Extra", "variant": "dark" }""");

        var resolver = CreateResolver();

        var files = resolver.Enumerate("themes", "*.theme.json");

        Assert.Contains("metalforge-dark.theme.json", files);
        Assert.Contains("extra.theme.json", files);
        // 每个相对路径只出现一次，且高优先级层先返回
        Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(
            IndexOf(files, "extra.theme.json") < IndexOf(files, "metalforge-dark.theme.json"),
            "用户层文件应排在更前面：" + string.Join(", ", files));
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    [Fact]
    public async Task BrandingWindowTitle_UsesTemplate()
    {
        using var service = new ConfigurationService(CreateResolver());
        await service.ReloadAsync(TestContext.Current.CancellationToken);

        var branding = service.Current.Branding;
        Assert.Equal("kernel.c — MetalForge", branding.FormatWindowTitle("kernel.c"));
        Assert.Equal("kernel.c • — MetalForge", branding.FormatWindowTitle("kernel.c", isDirty: true));
        Assert.Equal("MetalForge", branding.FormatWindowTitle());
    }

    public void Dispose()
    {
        TryDelete(_userDirectory);
        TryDelete(_projectDirectory);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
