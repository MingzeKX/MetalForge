using MetalForge.App.ViewModels;
using MetalForge.Core.Configuration;
using MetalForge.Core.Layout;
using MetalForge.Core.Localization;
using MetalForge.Core.Theming;

namespace MetalForge.Core.Tests;

/// <summary>
/// 界面装配测试。
///
/// 这里测的不是"某个控件长什么样"，而是**用户能看到的内容是否正确**：
/// 菜单/工具栏是否有重复文案、状态栏是否有条目、文档标签页是否初始化。
/// 这类缺陷不会让程序崩溃，只会让人困惑（本项目就出现过工具栏两个"启动 QEMU"，
/// 原因是一个命令复用了另一个命令的文案键）。
/// </summary>
public sealed class ShellCompositionTests : IDisposable
{
    private readonly string _userDirectory;
    private readonly AssetResolver _resolver;
    private readonly ConfigurationService _configuration;
    private readonly LocalizationService _localization;

    public ShellCompositionTests()
    {
        _userDirectory = Path.Combine(Path.GetTempPath(), "metalforge-shell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_userDirectory);

        _resolver = new AssetResolver(new AssetOptions
        {
            BuiltInAssetsDirectory = TestPaths.AssetsDirectory,
            UserDirectory = _userDirectory,
        });

        _configuration = new ConfigurationService(_resolver);
        _configuration.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();

        _localization = new LocalizationService(_resolver, new LocalizationOptions());
        _localization.Reload();
    }

    private ShellViewModel CreateShell()
        // 布局工厂返回 null 控件即可：本测试只关心菜单/工具栏/状态栏的数据，
        // 不构建视觉树（那需要 Avalonia 平台初始化）。
        => new(_configuration, _localization, _ => null!, new EditorViewModel(_localization, _ => null));

    [Fact]
    public void Rebuild_ProducesMenuToolbarAndStatusEntries()
    {
        var shell = CreateShell();
        shell.Rebuild();

        Assert.True(shell.MenuItems.Count >= 6, $"菜单项过少：{shell.MenuItems.Count}");
        Assert.True(shell.ToolbarItems.Count >= 8, $"工具栏条目过少：{shell.ToolbarItems.Count}");
        Assert.True(shell.StatusItems.Count >= 5, $"状态栏条目过少：{shell.StatusItems.Count}");

        // 每个顶层菜单都必须有子项，否则点开是空的
        Assert.All(shell.MenuItems, menu => Assert.True(menu.HasChildren, $"菜单 '{menu.Label}' 没有子项"));
    }

    [Fact]
    public void Toolbar_HasNoDuplicateVisibleLabels()
    {
        // 回归守卫：工具栏曾出现两个"启动 QEMU"（debug.start 复用了 run.start 的文案）。
        // 用户看到两个一模一样的按钮，完全无法区分它们。
        var shell = CreateShell();
        shell.Rebuild();

        var visibleLabels = shell.ToolbarItems
            .Where(item => !item.IsSeparator)
            .Select(item => item.Label)
            .ToArray();

        var duplicates = visibleLabels
            .GroupBy(label => label, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ×{group.Count()}")
            .ToArray();

        Assert.True(
            duplicates.Length == 0,
            "工具栏存在重复文案，用户无法区分：" + string.Join("、", duplicates));
    }

    [Fact]
    public void Toolbar_HasNoDuplicateCommandIds()
    {
        // 同一命令在工具栏出现两次同理：用户无法判断该点哪个。
        var shell = CreateShell();
        shell.Rebuild();

        var ids = shell.ToolbarItems
            .Where(item => !item.IsSeparator)
            .Select(item => item.CommandId)
            .ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void UnimplementedCommands_AreDisabledWithAReason()
    {
        // 项目纪律：不给"看起来能用、点了没反应"的按钮。
        // 未实现的命令必须显式禁用，并说明原因。
        var shell = CreateShell();
        shell.Rebuild();

        var disabledWithoutReason = shell.MenuItems
            .SelectMany(menu => menu.Children)
            .Where(item => !item.IsSeparator && !item.IsEnabled && string.IsNullOrWhiteSpace(item.DisabledReason))
            .Select(item => item.Label)
            .ToArray();

        Assert.True(
            disabledWithoutReason.Length == 0,
            "以下禁用条目没有说明原因：" + string.Join("、", disabledWithoutReason));
    }

    [Fact]
    public void EnabledCommands_HaveAnAction()
    {
        // 反向守卫：声称可用的条目必须真的有执行体。
        var shell = CreateShell();
        shell.Rebuild();

        var enabledWithoutAction = shell.MenuItems
            .SelectMany(menu => menu.Children)
            .Where(item => !item.IsSeparator && item.IsEnabled && item.Execute is null)
            .Select(item => item.Label)
            .ToArray();

        Assert.True(
            enabledWithoutAction.Length == 0,
            "以下条目声称可用但没有动作：" + string.Join("、", enabledWithoutAction));
    }

    [Fact]
    public void ViewMenu_OffersEveryConfiguredThemeAndLayoutAndLanguage()
    {
        // 项目要求"改 JSON 即可换主题/布局/语言"，因此这些必须出现在菜单里，
        // 否则用户无从发现。
        var shell = CreateShell();
        shell.Rebuild();

        var viewMenu = shell.MenuItems.Single(menu => menu.Label == _localization["menu.view.title"]);

        var themeMenu = viewMenu.Children.Single(item => item.Label == _localization["menu.view.items.theme"]);
        Assert.Equal(_configuration.AvailableThemes.Count, themeMenu.Children.Count);

        var layoutMenu = viewMenu.Children.Single(item => item.Label == _localization["menu.view.items.layout"]);
        Assert.Equal(_configuration.AvailableLayouts.Count, layoutMenu.Children.Count);

        var languageMenu = viewMenu.Children.Single(item => item.Label == _localization["menu.view.items.language"]);
        Assert.Equal(_localization.AvailableLanguages.Count, languageMenu.Children.Count);
    }

    [Fact]
    public void SwitchingThemeThroughMenu_UpdatesCurrentTheme()
    {
        var shell = CreateShell();
        shell.Rebuild();

        var viewMenu = shell.MenuItems.Single(menu => menu.Label == _localization["menu.view.title"]);
        var themeMenu = viewMenu.Children.Single(item => item.Label == _localization["menu.view.items.theme"]);

        // 选一个与当前不同的主题
        var target = themeMenu.Children.First(item => item.Label != _configuration.CurrentTheme.DisplayName);
        target.Execute?.Invoke();

        Assert.Equal(target.Label, _configuration.CurrentTheme.DisplayName);
        Assert.Equal(target.Label, shell.ThemeDisplayName);
    }

    [Fact]
    public void SwitchingLayout_ChangesVisibleChrome()
    {
        var shell = CreateShell();
        shell.Rebuild();

        var layouts = _configuration.AvailableLayouts;
        Assert.True(layouts.Count >= 2, "至少应有 default 与 debug 两个布局");

        var minimal = layouts.Single(layout => layout.Id == "debug");
        shell.ApplyLayout(minimal);

        Assert.Equal("debug", shell.CurrentLayout.Id);
        Assert.Equal(minimal.DisplayName, shell.LayoutDisplayName);
    }

    [Fact]
    public void Rebuild_AfterLanguageSwitch_RebuildsLocalizedContent()
    {
        var shell = CreateShell();
        shell.Rebuild();

        var chineseLabel = shell.MenuItems[0].Label;
        Assert.Equal(_localization["menu.file.title"], chineseLabel);

        Assert.True(_localization.TrySetLanguage("en"));

        // 语言切换事件应触发重建，菜单文案随之变化
        Assert.Equal("File", shell.MenuItems[0].Label);
        Assert.NotEqual(chineseLabel, shell.MenuItems[0].Label);
    }

    [Fact]
    public void WelcomeDocument_IsOpenedOnStartup()
    {
        var shell = CreateShell();
        shell.Rebuild();

        Assert.NotEmpty(shell.Documents);
        Assert.NotNull(shell.ActiveDocument);
        Assert.Equal("welcome", shell.Documents[0].TabId);
    }

    [Fact]
    public void StatusBar_ToolchainEntryShowsReadiness()
    {
        var shell = CreateShell();
        shell.Rebuild();

        var toolchainItem = shell.StatusItems.Single(item => item.CommandId == "status.toolchain");

        // 尚未探测时显示"缺少工具链"
        Assert.Equal(_localization["status.toolchainMissing"], toolchainItem.Description);
    }

    [Fact]
    public void EveryStatusItem_HasALabelAndValue()
    {
        var shell = CreateShell();
        shell.Rebuild();

        Assert.All(shell.StatusItems, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Label), $"{item.CommandId} 缺少标签");
            // Description 承载"值"；可以为 "—"，但不能是 null
            Assert.NotNull(item.Description);
        });
    }

    [Fact]
    public void BuiltInThemes_DefineEverySyntaxColorRole()
    {
        // 语法高亮由主题配色渲染 XSHD；主题漏配角色会直接导致某个语言失去高亮
        // （渲染时会产出 INVALID 颜色并报 MFHL002）。这里在数据层拦住。
        foreach (var theme in _configuration.AvailableThemes)
        {
            var missing = theme.Syntax.MissingRoles();
            Assert.True(
                missing.Count == 0,
                $"主题 '{theme.Id}' 缺少语法配色角色：{string.Join(", ", missing)}");

            foreach (var role in ThemeSyntaxColors.Roles)
            {
                var value = theme.Syntax[role]!;
                Assert.True(
                    HexColor.TryParse(value, out _),
                    $"主题 '{theme.Id}' 的 syntax.{role} = '{value}' 不是合法颜色");
            }
        }
    }

    public void Dispose()
    {
        _configuration.Dispose();
        _localization.Dispose();

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
