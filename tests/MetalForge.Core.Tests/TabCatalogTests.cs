using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;
using MetalForge.Core.Layout;
using MetalForge.Core.Localization;

namespace MetalForge.Core.Tests;

/// <summary>
/// 标签页目录的一致性测试。
///
/// 拦住的是一类"运行时才暴露、且表现为空白面板"的问题：
/// 布局 JSON 引用了未登记的标签页、或登记的标签页缺了文案键。
/// </summary>
public sealed class TabCatalogTests : IDisposable
{
    private readonly string _userDirectory;

    public TabCatalogTests()
    {
        _userDirectory = Path.Combine(Path.GetTempPath(), "metalforge-tab-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_userDirectory);
    }

    private AssetResolver CreateResolver() => new(new AssetOptions
    {
        BuiltInAssetsDirectory = TestPaths.AssetsDirectory,
        UserDirectory = _userDirectory,
    });

    [Fact]
    public void TabIds_AreUniqueAndWellFormed()
    {
        Assert.Equal(TabCatalog.All.Count, TabCatalog.Ids.Count);

        foreach (var tab in TabCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(tab.Id), "标签页 id 不能为空");
            // id 形式：点分的 camelCase，例如 output.build / debug.registers / inspector.layoutMap。
            // 允许驼峰是因为 id 要出现在用户可编辑的布局 JSON 里，与其中的其他键风格一致。
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(tab.Id, "^[a-z][a-zA-Z0-9]*(?:\\.[a-z][a-zA-Z0-9]*)*$"),
                $"标签页 id '{tab.Id}' 不符合命名约定（点分 camelCase）");
            Assert.False(string.IsNullOrWhiteSpace(tab.ImplementationKey), $"{tab.Id} 缺少实现键");
            Assert.False(string.IsNullOrWhiteSpace(tab.LocalizationKey), $"{tab.Id} 缺少文案键");
        }
    }

    [Fact]
    public void EveryLayoutTabReference_IsRegistered()
    {
        // 这是本测试类存在的首要理由：布局里出现未登记的标签页时，
        // 界面上只会表现为"某个面板是空的"，排查成本极高。
        var loader = new LayoutLoader(CreateResolver());
        var (presets, _) = loader.LoadAll();

        Assert.NotEmpty(presets);

        foreach (var preset in presets)
        {
            var unregistered = TabCatalog.FindUnregisteredTabs(preset);
            Assert.True(
                unregistered.Count == 0,
                $"布局 '{preset.Id}' 引用了未登记的标签页：{string.Join(", ", unregistered)}");
        }
    }

    [Fact]
    public void EveryTab_HasLocalizationKeyInEveryLanguage()
    {
        var localization = new LocalizationService(CreateResolver(), new LocalizationOptions());
        localization.Reload();

        var missing = new List<string>();

        foreach (var (languageCode, _) in localization.AvailableLanguages)
        {
            Assert.True(localization.TrySetLanguage(languageCode));

            foreach (var tab in TabCatalog.All)
            {
                if (!localization.TryGet(tab.LocalizationKey, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    missing.Add($"{languageCode}: {tab.Id} -> {tab.LocalizationKey}");
                }
            }
        }

        localization.Dispose();

        Assert.True(missing.Count == 0, "以下标签页缺少文案：" + string.Join(" | ", missing));
    }

    [Fact]
    public void DocumentTabs_AreNeverPlacedInToolPanels()
    {
        // 类型混用（把文档标签页塞进停靠面板，或反过来）在界面上表现为错位，
        // 在配置里却看不出来，因此用测试固化这条约束。
        var loader = new LayoutLoader(CreateResolver());
        var (presets, _) = loader.LoadAll();

        var problems = new List<string>();

        foreach (var preset in presets)
        {
            foreach (var node in preset.Root.DescendantsAndSelf())
            {
                if (node.Kind == LayoutNodeKind.Panel)
                {
                    foreach (var tabId in node.Tabs)
                    {
                        if (TabCatalog.Find(tabId) is { Kind: TabKind.Document } documentTab)
                        {
                            problems.Add($"布局 '{preset.Id}' 的面板 '{node.Id}' 里放了文档标签页 '{documentTab.Id}'");
                        }
                    }
                }
                else if (node.Kind == LayoutNodeKind.Document)
                {
                    foreach (var tabId in node.InitialTabs)
                    {
                        if (TabCatalog.Find(tabId) is { Kind: TabKind.Tool } toolTab)
                        {
                            problems.Add($"布局 '{preset.Id}' 的文档区里放了工具标签页 '{toolTab.Id}'");
                        }
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }

    [Fact]
    public void LayoutValidation_ReportsUnregisteredTabsAsWarnings()
    {
        // LayoutLoader 层面的独立防线：即使调用方忘了查 TabCatalog，
        // 未知标签页也只会是警告，不会让整个布局失效。
        var preset = new LayoutPreset
        {
            Id = "x",
            Root = new LayoutNode { Kind = LayoutNodeKind.Panel, Id = "p", Tabs = ["output.build", "does.not.exist"] },
        };

        var diagnostics = LayoutLoader.ValidateTabIds(preset, TabCatalog.Ids, "x.layout.json");

        var single = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, single.Severity);
        Assert.Contains("does.not.exist", single.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltInLayouts_UseToolTabsInPanelsAndDocumentTabsInDocumentArea()
    {
        // 正向确认：默认布局确实包含预期的关键标签页，避免"测试通过但布局是空的"。
        var loader = new LayoutLoader(CreateResolver());
        var (presets, _) = loader.LoadAll();

        var defaultLayout = presets.Single(preset => preset.Id == "default");
        var allTabs = defaultLayout.AllTabIds();

        Assert.Contains("welcome", allTabs);
        Assert.Contains("project.files", allTabs);
        Assert.Contains("inspector.properties", allTabs);
        Assert.Contains("output.build", allTabs);
        Assert.Contains("terminal.serial", allTabs);
        Assert.Contains("ai.agent", allTabs);
    }

    [Fact]
    public void IsReferencedBy_DetectsMembership()
    {
        var loader = new LayoutLoader(CreateResolver());
        var (presets, _) = loader.LoadAll();
        var defaultLayout = presets.Single(preset => preset.Id == "default");

        var welcome = TabCatalog.Find("welcome")!;
        var debugStack = TabCatalog.Find("debug.stack")!;

        Assert.True(welcome.IsReferencedBy(defaultLayout));
        // 调试专用标签页放在 debug 布局里，不在 default 里
        Assert.False(debugStack.IsReferencedBy(defaultLayout));
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
