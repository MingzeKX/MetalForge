using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;
using MetalForge.Core.Localization;

namespace MetalForge.Core.Tests;

public sealed class LocalizationServiceTests : IDisposable
{
    private readonly string _userDirectory;

    public LocalizationServiceTests()
    {
        _userDirectory = Path.Combine(Path.GetTempPath(), "metalforge-locale-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_userDirectory);
    }

    private AssetResolver CreateResolver() => new(new AssetOptions
    {
        BuiltInAssetsDirectory = TestPaths.AssetsDirectory,
        UserDirectory = _userDirectory,
    });

    private LocalizationService CreateService(string defaultLanguage = "zh-Hans")
    {
        var service = new LocalizationService(CreateResolver(), new LocalizationOptions(DefaultLanguage: defaultLanguage));
        service.Reload();
        return service;
    }

    private void WriteUserLocale(string fileName, string content)
    {
        var directory = Path.Combine(_userDirectory, "locales");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content, System.Text.Encoding.UTF8);
    }

    [Fact]
    public void LoadsBuiltInLanguages()
    {
        using var service = CreateService();

        var errors = service.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, "内置语言文件产生了错误：" + string.Join(" | ", errors.Select(d => d.ToString())));

        Assert.Contains(service.AvailableLanguages, l => l.LanguageCode == "zh-Hans");
        Assert.Contains(service.AvailableLanguages, l => l.LanguageCode == "en");
    }

    [Fact]
    public void SchemaFileInLocaleDirectory_IsNotTreatedAsLanguageFile()
    {
        // locales/ 目录里同时放着 schema/locale.schema.json；
        // 若被当成语言文件，每次加载都会报一条"缺少 language 字段"的假错误。
        using var service = CreateService();

        Assert.DoesNotContain(service.Diagnostics, d => d.Code == "MFLOC001");
        Assert.DoesNotContain(service.AvailableLanguages, l => l.LanguageCode.Contains("schema", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DefaultLanguage_IsChinese()
    {
        using var service = CreateService();

        Assert.Equal("zh-Hans", service.CurrentLanguage);
        Assert.Equal("文件", service["menu.file.title"]);
        Assert.Equal("新建项目…", service["menu.file.items.newProject"]);
    }

    [Fact]
    public void SwitchingLanguage_ChangesLookupsAndRaisesEvent()
    {
        using var service = CreateService();
        var raised = new List<string>();
        service.LanguageChanged += (_, language) => raised.Add(language);

        Assert.True(service.TrySetLanguage("en"));

        Assert.Equal("en", service.CurrentLanguage);
        Assert.Equal("File", service["menu.file.title"]);
        Assert.Equal("New Project…", service["menu.file.items.newProject"]);
        Assert.Equal(["en"], raised);
    }

    [Fact]
    public void UnknownLanguage_IsRejectedAndKeepsCurrent()
    {
        using var service = CreateService();

        Assert.False(service.TrySetLanguage("xx-YY"));
        Assert.Equal("zh-Hans", service.CurrentLanguage);
    }

    [Fact]
    public void MissingKey_FallsBackToKeyNameSoGapsAreVisible()
    {
        using var service = CreateService();

        // 返回键名而不是空串：界面上显示 menu.nope 能立刻指出缺失的翻译，空白则像界面坏了。
        Assert.Equal("menu.nope.not.here", service["menu.nope.not.here"]);
        Assert.False(service.TryGet("menu.nope.not.here", out _));
    }

    [Fact]
    public void Format_SubstitutesPlaceholders()
    {
        using var service = CreateService();

        var text = service.Format("status.errorsAndWarnings", 3, 5);

        Assert.Equal("3 个错误，5 个警告", text);
    }

    [Fact]
    public void Format_WithBadPlaceholder_DoesNotThrow()
    {
        WriteUserLocale(
            "zh-Hans.json",
            """
            {
              "language": "zh-Hans",
              "displayName": "简体中文",
              "strings": { "broken": "值 {o} 不合法" }
            }
            """);

        using var service = CreateService();

        // 翻译文件把 {0} 写成 {o}：不应崩溃，且要留下诊断
        var text = service.Format("broken", 42);

        Assert.Contains("{o}", text, StringComparison.Ordinal);
        Assert.Contains(service.Diagnostics, d => d.Code == "MFLOC002");
    }

    [Fact]
    public void MissingKeyInActiveLanguage_FallsBackToDefaultLanguage()
    {
        // 场景：用户新增一门只翻译了一部分的语言。
        //
        // 注意内置的 en.json 是完整的，因此不能用"英文里缺的键"来测回退
        // ——那会命中内置英文层而不是默认语言层，测出来的结论是假的
        // （本项目第一版就是这么写的，断言失败才发现测错了对象）。
        // 这里新造一门只有一条文案的语言，被查找的键在整个 English 层都不存在，
        // 回退来源因此唯一确定为默认语言 zh-Hans。
        WriteUserLocale(
            "fr.json",
            """
            {
              "language": "fr",
              "displayName": "Français (partiel)",
              "strings": { "menu": { "file": { "title": "Fichier" } } }
            }
            """);

        using var service = CreateService("zh-Hans");
        Assert.True(service.TrySetLanguage("fr"));

        // 用户层显式提供的键
        Assert.Equal("Fichier", service["menu.file.title"]);

        // English 层完全没有这个键 → 必须回退到默认语言 zh-Hans
        Assert.Equal("工具", service["menu.tools.title"]);
        Assert.Equal("全部就绪", service["toolchain.statusReady"]);
    }

    [Fact]
    public void PartialLanguage_IsMergedOverDefaultLanguageNotReplacingIt()
    {
        // 深合并 vs 整组替换：用户层只给出 menu.file.title，
        // 若实现错误地"整组替换 menu"，其余菜单会全部消失。
        WriteUserLocale(
            "fr.json",
            """
            {
              "language": "fr",
              "displayName": "Français (partiel)",
              "strings": { "menu": { "file": { "items": { "newProject": "Nouveau projet…" } } } }
            }
            """);

        using var service = CreateService("zh-Hans");
        Assert.True(service.TrySetLanguage("fr"));

        Assert.Equal("Nouveau projet…", service["menu.file.items.newProject"]);
        // 同组内未被覆盖的兄弟键仍然来自默认语言
        Assert.Equal("文件", service["menu.file.title"]);
        Assert.Equal("打开项目…", service["menu.file.items.openProject"]);
        Assert.Equal("构建", service["menu.build.title"]);
    }

    [Fact]
    public void LocaleWithoutLanguageField_IsReportedAndIgnored()
    {
        WriteUserLocale(
            "broken.json",
            """
            { "displayName": "没有 language 字段", "strings": { "a": "b" } }
            """);

        using var service = CreateService();

        var problem = service.Diagnostics.FirstOrDefault(d => d.Code == "MFLOC001");
        Assert.NotNull(problem);
        Assert.Contains("language", problem.Message, StringComparison.Ordinal);
        // 内置语言仍然可用
        Assert.Equal("zh-Hans", service.CurrentLanguage);
    }

    [Fact]
    public void AllBuiltInLanguages_CoverTheSameKeySet()
    {
        // 直接比较内置语言文件本身，不受用户层覆盖影响（覆盖会合并默认语言，
        // 从而掩盖"英文漏翻"这类问题）。
        var builtInEnglish = ReadBuiltInKeys("en.json");
        var builtInChinese = ReadBuiltInKeys("zh-Hans.json");

        var missingInEnglish = builtInChinese.Except(builtInEnglish, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var missingInChinese = builtInEnglish.Except(builtInChinese, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            missingInEnglish.Length == 0,
            $"英文缺少 {missingInEnglish.Length} 个键：" + string.Join(", ", missingInEnglish.Take(20)));
        Assert.True(
            missingInChinese.Length == 0,
            $"中文缺少 {missingInChinese.Length} 个键：" + string.Join(", ", missingInChinese.Take(20)));
    }

    private static List<string> ReadBuiltInKeys(string fileName)
    {
        var path = TestPaths.Asset("locales", fileName);
        Assert.True(File.Exists(path), $"找不到内置语言文件：{path}");

        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
        if (node?["strings"] is not System.Text.Json.Nodes.JsonObject strings)
        {
            return [];
        }

        var keys = new List<string>();
        CollectKeys(strings, string.Empty, keys);
        return keys;
    }

    private static void CollectKeys(System.Text.Json.Nodes.JsonObject node, string prefix, List<string> keys)
    {
        foreach (var pair in node)
        {
            var path = prefix.Length == 0 ? pair.Key : prefix + "." + pair.Key;

            if (pair.Value is System.Text.Json.Nodes.JsonObject child)
            {
                CollectKeys(child, path, keys);
            }
            else
            {
                keys.Add(path);
            }
        }
    }

    [Fact]
    public void NoMissingTranslations_ForKnownUiKeys()
    {
        using var service = CreateService();

        // 抽查一批界面一定会用到的键，确保没有漏翻（漏了界面会直接显示键名）
        string[] requiredKeys =
        [
            "menu.file.title", "menu.build.title", "menu.run.title", "menu.tools.title", "menu.help.title",
            "panel.explorer", "panel.inspector", "panel.bottom",
            "tab.welcome", "tab.toolchainHealth",
            "status.noProject", "status.toolchainReady", "status.buildIdle",
            "welcome.nextSteps", "explorer.empty", "inspector.noSelection",
            "output.empty", "problems.empty", "terminal.waitingForOutput",
            "toolchain.title", "toolchain.missing", "toolchain.refresh",
            "dialog.ok", "dialog.cancel", "settings.title",
        ];

        foreach (var key in requiredKeys)
        {
            Assert.True(service.TryGet(key, out var value), $"缺少文案键：{key}");
            Assert.False(string.IsNullOrWhiteSpace(value), $"文案键 {key} 的值为空");
        }
    }

    [Fact]
    public void Keys_ReturnsFlattenedPathsOfActiveLanguage()
    {
        using var service = CreateService();

        var keys = service.Keys();

        Assert.Contains("menu.file.items.newProject", keys);
        Assert.Contains("toolchain.statusReady", keys);
        Assert.DoesNotContain(keys, key => key.Contains("..", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_userDirectory))
            {
                Directory.Delete(_userDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }
}
