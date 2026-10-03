using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetalForge.Core.Configuration;
using MetalForge.Core.Layout;
using MetalForge.Core.Localization;

namespace MetalForge.App.ViewModels;

/// <summary>
/// 外壳 ViewModel：把"配置里的布局预设"变成"界面上真实可用的菜单、工具栏与面板状态"。
///
/// 职责边界：这个类不认识具体控件（除了持有已构建好的布局根控件），
/// 也不直接读写文件。它回答三个问题：
///   1) 当前用哪个布局、哪些主题/语言可选；
///   2) 菜单与工具栏该显示什么、哪些命令可用；
///   3) 文档区有哪些标签页（委托给 <see cref="DocumentAreaViewModel"/>）。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IConfigurationService _configuration;
    private readonly ILocalizationService _localization;
    private readonly Func<LayoutPreset, Control> _layoutFactory;
    private readonly EditorViewModel _editor;

    [ObservableProperty]
    private string _windowTitle = "MetalForge";

    [ObservableProperty]
    private IReadOnlyList<CommandItemViewModel> _menuItems = [];

    [ObservableProperty]
    private IReadOnlyList<CommandItemViewModel> _toolbarItems = [];

    [ObservableProperty]
    private IReadOnlyList<CommandItemViewModel> _statusItems = [];

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _layoutDisplayName = string.Empty;

    [ObservableProperty]
    private string _themeDisplayName = string.Empty;

    [ObservableProperty]
    private string _languageDisplayName = string.Empty;

    [ObservableProperty]
    private Control? _layoutRoot;

    [ObservableProperty]
    private bool _showMenuBar = true;

    [ObservableProperty]
    private bool _showToolbar = true;

    [ObservableProperty]
    private bool _showStatusBar = true;

    /// <summary>当前在编辑器里显示的文档（最近一次打开的文件）。</summary>
    [ObservableProperty]
    private EditorDocumentViewModel? _activeEditorDocument;

    public ShellViewModel(
        IConfigurationService configuration,
        ILocalizationService localization,
        Func<LayoutPreset, Control> layoutFactory,
        EditorViewModel editor,
        DocumentAreaViewModel documentArea)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(layoutFactory);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(documentArea);

        _configuration = configuration;
        _localization = localization;
        _layoutFactory = layoutFactory;
        _editor = editor;
        DocumentArea = documentArea;

        _localization.LanguageChanged += (_, _) => RebuildLocalizedContent();
        _configuration.ConfigurationChanged += (_, _) => RebuildLocalizedContent();
    }

    /// <summary>文档区（多标签编辑器）。它是"已打开的文件"的唯一真相源。</summary>
    public DocumentAreaViewModel DocumentArea { get; }

    /// <summary>当前生效的布局预设。</summary>
    public LayoutPreset CurrentLayout { get; private set; } = new();

    /// <summary>可用布局预设。</summary>
    public IReadOnlyList<LayoutPreset> AvailableLayouts => _configuration.AvailableLayouts;

    /// <summary>可选主题。</summary>
    public IReadOnlyList<Core.Theming.ThemeDefinition> AvailableThemes => _configuration.AvailableThemes;

    /// <summary>可选语言。</summary>
    public IReadOnlyList<(string LanguageCode, string DisplayName)> AvailableLanguages => _localization.AvailableLanguages;

    /// <summary>
    /// "打开文件…"的请求事件。由宿主（窗口）接管：ViewModel 不碰窗口与对话框，
    /// 这是保持 ViewModel 可单元测试的分界线。
    /// </summary>
    public event EventHandler? FileOpenRequested;

    /// <summary>按当前配置与语言重建全部界面内容。启动时与配置/语言变化时调用。</summary>
    public void Rebuild(LayoutPreset? layoutOverride = null)
    {
        var branding = _configuration.Current.Branding;
        CurrentLayout = layoutOverride ?? ResolveStartupLayout();

        WindowTitle = branding.FormatWindowTitle();
        ShowMenuBar = CurrentLayout.MenuBar;
        ShowToolbar = CurrentLayout.Toolbar.Visible;
        ShowStatusBar = CurrentLayout.StatusBar.Visible;
        LayoutDisplayName = CurrentLayout.DisplayName;
        ThemeDisplayName = _configuration.CurrentTheme.DisplayName;
        LanguageDisplayName = CurrentLanguageDisplayName();

        LayoutRoot = _layoutFactory(CurrentLayout);

        MenuItems = BuildMenu();
        ToolbarItems = BuildToolbar(CurrentLayout.Toolbar);
        StatusItems = BuildStatusBar(CurrentLayout.StatusBar);
        StatusText = BuildStatusText();

        DocumentArea.EnsureWelcome();
    }

    /// <summary>切换布局预设（菜单"视图 → 布局预设"）。</summary>
    [RelayCommand]
    public void ApplyLayout(LayoutPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        Rebuild(preset);
    }

    /// <summary>切换主题。</summary>
    [RelayCommand]
    public void ApplyTheme(string themeId)
    {
        if (_configuration.TryApplyTheme(themeId))
        {
            ThemeDisplayName = _configuration.CurrentTheme.DisplayName;
        }
    }

    /// <summary>切换界面语言。</summary>
    [RelayCommand]
    public void ApplyLanguage(string languageCode)
    {
        _localization.TrySetLanguage(languageCode);
    }

    /// <summary>打开"关于"。</summary>
    [RelayCommand]
    public void OpenAbout() => DocumentArea.OpenContentTab("about", _localization["tab.about"]);

    /// <summary>打开"设置"。</summary>
    [RelayCommand]
    public void OpenSettings() => DocumentArea.OpenContentTab("settings", _localization["tab.settings"]);

    /// <summary>保存当前编辑器文档。</summary>
    [RelayCommand]
    public void SaveActiveDocument()
    {
        if (ActiveEditorDocument is { } document)
        {
            EditorViewModel.Save(document);
        }
    }

    /// <summary>关闭当前文档标签页。</summary>
    [RelayCommand]
    public void CloseActiveDocument()
    {
        if (DocumentArea.ActiveTab is { } tab)
        {
            DocumentArea.CloseTab(tab);
        }
    }

    /// <summary>
    /// 在文档区打开文件：新增（或聚焦）一个编辑器标签页并把选中项切过去，
    /// 因此用户立刻能看到内容。
    /// </summary>
    public void OpenInEditor(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var document = _editor.Open(filePath);
        ActiveEditorDocument = document;

        var tab = DocumentArea.OpenFile(filePath, document);

        // 标签页上的"未保存"标记跟随文档状态。
        document.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EditorDocumentViewModel.IsDirty))
            {
                tab.IsDirty = document.IsDirty;
            }
        };
    }

    /// <summary>
    /// 执行一个菜单/工具栏条目。XAML 通过绑定到这里统一分发，
    /// 避免为每个条目生成一个 ICommand（条目数量会随里程碑增长）。
    /// </summary>
    [RelayCommand]
    private static void Invoke(CommandItemViewModel? item)
    {
        if (item is null || !item.IsEnabled || item.Execute is null)
        {
            // 禁用的条目不应有动作；这里静默返回而不是抛异常。
            return;
        }

        item.Execute();
    }

    private void OpenFileRequested() => FileOpenRequested?.Invoke(this, EventArgs.Empty);

    private void RebuildLocalizedContent()
    {
        // 文档区由自己的 ViewModel 管理，重建布局不会丢标签页；只需刷新可本地化的标题。
        foreach (var tab in DocumentArea.Tabs)
        {
            tab.Title = tab.TabId switch
            {
                "welcome" => _localization["tab.welcome"],
                "about" => _localization["tab.about"],
                "settings" => _localization["tab.settings"],
                _ => tab.Title,
            };
        }

        Rebuild();
    }

    private LayoutPreset ResolveStartupLayout()
    {
        var requested = _configuration.Current.Branding.Assets.Layout;
        var layouts = _configuration.AvailableLayouts;

        if (layouts.Count == 0)
        {
            // 没有任何布局文件：给出一个"只剩文档区"的最小布局，
            // 界面仍然可用，而不是直接崩掉。
            return new LayoutPreset
            {
                Id = "fallback",
                DisplayName = _localization["app.name"],
                Root = new LayoutNode { Kind = LayoutNodeKind.Document, InitialTabs = ["welcome"] },
            };
        }

        return layouts.FirstOrDefault(layout => string.Equals(layout.Id, requested, StringComparison.OrdinalIgnoreCase))
               ?? layouts[0];
    }

    private string CurrentLanguageDisplayName()
    {
        var current = _localization.CurrentLanguage;
        return _localization.AvailableLanguages
            .FirstOrDefault(language => string.Equals(language.LanguageCode, current, StringComparison.OrdinalIgnoreCase))
            .DisplayName ?? current;
    }

    private IReadOnlyList<CommandItemViewModel> BuildMenu() =>
    [
        Menu("menu.file.title",
            Item("menu.file.items.newProject"),
            Item("menu.file.items.openProject"),
            Item("menu.file.items.openFile", gesture: "Ctrl+O", Execute: OpenFileRequested),
            Item("menu.file.items.closeProject"),
            Separator(),
            Item("menu.file.items.save", gesture: "Ctrl+S", Execute: SaveActiveDocument),
            Item("menu.file.items.saveAll", gesture: "Ctrl+Shift+S"),
            Separator(),
            Item("menu.file.items.closeDocument", gesture: "Ctrl+W", Execute: CloseActiveDocument),
            Item("menu.file.items.exit")),

        Menu("menu.edit.title",
            Item("menu.edit.items.undo", gesture: "Ctrl+Z"),
            Item("menu.edit.items.redo", gesture: "Ctrl+Y"),
            Separator(),
            Item("menu.edit.items.cut", gesture: "Ctrl+X"),
            Item("menu.edit.items.copy", gesture: "Ctrl+C"),
            Item("menu.edit.items.paste", gesture: "Ctrl+V"),
            Separator(),
            Item("menu.edit.items.find", gesture: "Ctrl+F"),
            Item("menu.edit.items.replace", gesture: "Ctrl+H"),
            Item("menu.edit.items.formatDocument", gesture: "Shift+Alt+F")),

        Menu("menu.view.title",
            Item("menu.view.items.explorer", gesture: "Ctrl+Shift+E"),
            Item("menu.view.items.inspector"),
            Item("menu.view.items.output", gesture: "Ctrl+`"),
            Item("menu.view.items.terminal"),
            Separator(),
            SubMenu("menu.view.items.layout", [.. AvailableLayouts.Select(layout => new CommandItemViewModel
            {
                CommandId = $"view.layout.{layout.Id}",
                Label = layout.DisplayName,
                Execute = () => ApplyLayout(layout),
            })]),
            SubMenu("menu.view.items.theme", [.. AvailableThemes.Select(theme => new CommandItemViewModel
            {
                CommandId = $"view.theme.{theme.Id}",
                Label = theme.DisplayName,
                Description = theme.Description,
                Execute = () => ApplyTheme(theme.Id),
            })]),
            SubMenu("menu.view.items.language", [.. AvailableLanguages.Select(language => new CommandItemViewModel
            {
                CommandId = $"view.language.{language.LanguageCode}",
                Label = language.DisplayName,
                Execute = () => ApplyLanguage(language.LanguageCode),
            })]),
            Separator(),
            Item("menu.view.items.resetLayout", Execute: () => Rebuild())),

        Menu("menu.build.title",
            Item("menu.build.items.build", gesture: "Ctrl+Shift+B"),
            Item("menu.build.items.rebuild"),
            Separator(),
            SubMenu("menu.build.items.clean",
            [
                Item("menu.build.items.cleanOutputs"),
                Item("menu.build.items.cleanAll"),
            ]),
            Item("menu.build.items.configuration")),

        Menu("menu.run.title",
            Item("menu.run.items.start", gesture: "F5"),
            Item("menu.run.items.restart", gesture: "Ctrl+Shift+F5"),
            Item("menu.run.items.stop", gesture: "Shift+F5")),

        Menu("menu.tools.title",
            Item("menu.tools.items.toolchainHealth", Execute: OpenToolchainHealth),
            Item("menu.tools.items.refreshDetection"),
            Separator(),
            Item("menu.tools.items.commandPalette", gesture: "Ctrl+Shift+P")),

        Menu("menu.help.title",
            Item("menu.help.items.documentation"),
            Item("menu.help.items.changelog"),
            Separator(),
            Item("menu.help.items.about", Execute: OpenAbout)),
    ];

    private IReadOnlyList<CommandItemViewModel> BuildToolbar(LayoutToolbar toolbar)
    {
        var items = new List<CommandItemViewModel>();

        foreach (var commandId in toolbar.Items)
        {
            if (string.Equals(commandId, "separator", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(Separator());
                continue;
            }

            items.Add(ItemForCommand(commandId));
        }

        return items;
    }

    private IReadOnlyList<CommandItemViewModel> BuildStatusBar(LayoutStatusBar statusBar)
    {
        var items = new List<CommandItemViewModel>();

        foreach (var itemId in statusBar.Items)
        {
            var (label, value, enabled) = itemId switch
            {
                "status.arch" => (_localization["status.arch"], _localization["status.noProject"], false),
                "status.bootMethod" => (_localization["status.bootMethod"], _localization["status.noProject"], false),
                "status.configuration" => (_localization["status.configuration"], "—", false),
                "status.toolchain" => (_localization["status.toolchain"], ToolchainSummary(), true),
                "status.buildState" => (_localization["status.buildState"], _localization["status.buildIdle"], false),
                "status.cursor" => (_localization["status.cursor"], ActiveEditorDocument?.CaretText ?? "—", true),
                "status.problems" => (_localization["status.problems"], _localization["status.noProblems"], true),
                "status.debugState" => (_localization["status.debugState"], "—", false),
                "status.currentFrame" => (_localization["status.currentFrame"], "—", false),
                _ => (itemId, "—", false),
            };

            items.Add(new CommandItemViewModel
            {
                CommandId = itemId,
                Label = label,
                Description = value,
                IsEnabled = enabled,
                Execute = enabled && itemId == "status.toolchain" ? OpenToolchainHealth : null,
            });
        }

        return items;
    }

    private string BuildStatusText()
    {
        var branding = _configuration.Current.Branding;
        return $"{branding.Name} {branding.Version} · {LayoutDisplayName} · {ThemeDisplayName} · {LanguageDisplayName}";
    }

    private string ToolchainSummary()
    {
        var report = _configuration.ToolHealth;
        if (report is null)
        {
            return _localization["status.toolchainMissing"];
        }

        return report.Readiness switch
        {
            Core.Toolchains.ToolchainReadiness.Ready => _localization["status.toolchainReady"],
            Core.Toolchains.ToolchainReadiness.Degraded => _localization["status.toolchainDegraded"],
            _ => _localization["status.toolchainMissing"],
        };
    }

    /// <summary>工具栏命令的可用性注册表。未实现的命令被显式禁用并给出原因。</summary>
    private CommandItemViewModel ItemForCommand(string commandId) => commandId switch
    {
        "project.new" => Item("menu.file.items.newProject", commandIdOverride: commandId),
        "project.open" => Item("menu.file.items.openProject", commandIdOverride: commandId),
        "build.build" => Item("menu.build.items.build", commandIdOverride: commandId),
        "build.rebuild" => Item("menu.build.items.rebuild", commandIdOverride: commandId),
        "build.clean" => Item("menu.build.items.clean", commandIdOverride: commandId),
        "run.qemu" => Item("menu.run.items.start", commandIdOverride: commandId),
        "run.stop" => Item("menu.run.items.stop", commandIdOverride: commandId),
        "debug.start" => Item("menu.run.items.debugStart", commandIdOverride: "debug.start"),
        "debug.stepOver" => Placeholder("debug.stepOver", "单步跳过"),
        "debug.stepInto" => Placeholder("debug.stepInto", "单步进入"),
        "debug.stop" => Placeholder("debug.stop", "停止调试"),
        "tools.toolchainHealth" => Item("menu.tools.items.toolchainHealth", Execute: OpenToolchainHealth, commandIdOverride: commandId),
        "view.commandPalette" => Item("menu.tools.items.commandPalette", commandIdOverride: commandId),
        _ => Placeholder(commandId, commandId),
    };

    private void OpenToolchainHealth()
        => DocumentArea.OpenContentTab("toolchain.health", _localization["tab.toolchainHealth"]);

    private CommandItemViewModel Menu(string titleKey, params CommandItemViewModel[] children) => new()
    {
        CommandId = titleKey,
        Label = _localization[titleKey],
        Children = children,
    };

    /// <summary>
    /// 子菜单容器（"布局预设"、"配色主题"…）。
    ///
    /// 它是可展开的容器，本身没有动作，因此必须**禁用**自身：
    /// 若标记为可用却点不出东西，用户会以为界面坏了。
    /// 用户通过展开后的子项来操作。
    /// </summary>
    private CommandItemViewModel SubMenu(string titleKey, IReadOnlyList<CommandItemViewModel> children) => new()
    {
        CommandId = titleKey,
        Label = _localization[titleKey],
        Children = children,
        IsEnabled = false,
        DisabledReason = _localization["menu.expandToChoose"],
    };

    private CommandItemViewModel Item(
        string labelKey,
        string? gesture = null,
        Action? Execute = null,
        string? commandIdOverride = null) => new()
    {
        CommandId = commandIdOverride ?? labelKey,
        Label = _localization[labelKey],
        Gesture = gesture,
        Execute = Execute,
        IsEnabled = Execute is not null,
        DisabledReason = Execute is null ? _localization["toolbar.notImplemented"] : null,
    };

    private CommandItemViewModel Placeholder(string commandId, string label) => new()
    {
        CommandId = commandId,
        Label = label,
        IsEnabled = false,
        DisabledReason = _localization["toolbar.notImplemented"],
    };

    private static CommandItemViewModel Separator() => new() { IsSeparator = true };
}
