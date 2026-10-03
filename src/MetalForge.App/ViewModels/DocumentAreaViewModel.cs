using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MetalForge.App.ViewModels;

/// <summary>
/// 文档区里的一个标签页。它只描述"这一个标签页是什么"，内容控件由工厂按需创建。
/// </summary>
public sealed partial class WorkspaceTabViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isClosable = true;

    /// <summary>稳定标识（欢迎页为 welcome，文件为规范化路径）。</summary>
    public required string TabId { get; init; }

    /// <summary>内容类型键，交给 <see cref="DocumentAreaViewModel"/> 解析成控件。</summary>
    public required string ContentKey { get; init; }

    /// <summary>文件标签页对应的编辑器文档；欢迎页等为 null。</summary>
    public EditorDocumentViewModel? EditorDocument { get; init; }

    public string DisplayTitle => IsDirty ? Title + " •" : Title;

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(DisplayTitle));
}

/// <summary>
/// 文档区（多标签编辑器）。它是"打开的文件"的唯一真相源。
///
/// 为什么要单独一个 ViewModel：此前文档区是布局构建器里一个固定的 TabControl
/// （只显示欢迎页），而 ShellViewModel 另有一份没人使用的 Documents 集合。
/// 结果是"打开文件"把文档加进了 A，界面却停在 B —— 功能实现了但看不见。
/// 现在布局只负责给出一个占位容器，真正的内容由这里驱动。
/// </summary>
public sealed partial class DocumentAreaViewModel : ObservableObject
{
    private readonly Func<WelcomeViewModel> _welcomeFactory;
    private readonly Func<ToolchainHealthViewModel> _toolchainFactory;
    private readonly Func<AboutViewModel> _aboutFactory;
    private readonly Func<SettingsViewModel> _settingsFactory;
    private readonly Func<EditorDocumentViewModel?> _activeEditorDocument;
    private readonly Func<string, string> _localize;

    [ObservableProperty]
    private WorkspaceTabViewModel? _activeTab;

    public DocumentAreaViewModel(
        Func<WelcomeViewModel> welcomeFactory,
        Func<ToolchainHealthViewModel> toolchainFactory,
        Func<AboutViewModel> aboutFactory,
        Func<SettingsViewModel> settingsFactory,
        Func<EditorDocumentViewModel?> activeEditorDocument,
        Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(welcomeFactory);
        ArgumentNullException.ThrowIfNull(toolchainFactory);
        ArgumentNullException.ThrowIfNull(aboutFactory);
        ArgumentNullException.ThrowIfNull(settingsFactory);
        ArgumentNullException.ThrowIfNull(activeEditorDocument);
        ArgumentNullException.ThrowIfNull(localize);

        _welcomeFactory = welcomeFactory;
        _toolchainFactory = toolchainFactory;
        _aboutFactory = aboutFactory;
        _settingsFactory = settingsFactory;
        _activeEditorDocument = activeEditorDocument;
        _localize = localize;
    }

    /// <summary>已打开的标签页。第一个标签页固定为欢迎页，不可关闭。</summary>
    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = [];

    /// <summary>确保欢迎页存在并选中它。</summary>
    public WorkspaceTabViewModel EnsureWelcome()
    {
        var existing = Tabs.FirstOrDefault(tab => tab.TabId == "welcome");
        if (existing is not null)
        {
            return existing;
        }

        var welcome = new WorkspaceTabViewModel
        {
            TabId = "welcome",
            ContentKey = "welcome",
            Title = _localize("tab.welcome"),
            IsClosable = false,
        };

        if (Tabs.Count == 0)
        {
            Tabs.Add(welcome);
        }
        else
        {
            Tabs.Insert(0, welcome);
        }

        ActiveTab ??= welcome;
        return welcome;
    }

    /// <summary>
    /// 打开（或聚焦）一个"内容标签页"（欢迎页之外的静态内容，例如"关于"、"设置"、工具链）。
    /// 这些标签页的内容由 <see cref="CreateContent"/> 按 <see cref="WorkspaceTabViewModel.ContentKey"/> 决定。
    /// </summary>
    public WorkspaceTabViewModel OpenContentTab(string contentKey, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var existing = Tabs.FirstOrDefault(tab => string.Equals(tab.TabId, contentKey, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveTab = existing;
            return existing;
        }

        var tab = new WorkspaceTabViewModel
        {
            TabId = contentKey,
            ContentKey = contentKey,
            Title = title,
        };

        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    /// <summary>
    /// 打开（或聚焦）一个文件标签页。
    /// 同一个文件只允许有一个标签页：重复打开应聚焦既有标签，而不是开出一堆副本。
    /// </summary>
    public WorkspaceTabViewModel OpenFile(string filePath, EditorDocumentViewModel document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(document);

        var normalized = Path.GetFullPath(filePath);
        var existing = Tabs.FirstOrDefault(tab =>
            string.Equals(tab.TabId, normalized, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            ActiveTab = existing;
            return existing;
        }

        var tab = new WorkspaceTabViewModel
        {
            TabId = normalized,
            ContentKey = "editor",
            Title = document.Title,
            IsDirty = document.IsDirty,
            EditorDocument = document,
        };

        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    /// <summary>关闭一个标签页。不可关闭的标签页（欢迎页）会被忽略。</summary>
    [RelayCommand]
    public void CloseTab(WorkspaceTabViewModel? tab)
    {
        if (tab is null || !tab.IsClosable)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);
        ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    /// <summary>按内容键创建控件。</summary>
    public Control? CreateContent(WorkspaceTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        return tab.ContentKey switch
        {
            "welcome" => new Views.WelcomeView { DataContext = _welcomeFactory() },
            "toolchain.health" => new Views.ToolchainHealthView { DataContext = _toolchainFactory() },
            "about" => new Views.AboutView { DataContext = _aboutFactory() },
            "settings" => new Views.SettingsView { DataContext = _settingsFactory() },
            "editor" => CreateEditorContent(tab),
            _ => null,
        };
    }

    private Control CreateEditorContent(WorkspaceTabViewModel tab)
    {
        // 标签页自己记录了文档；若没有（例如"新建文件"入口），用当前活动文档兜底。
        var document = tab.EditorDocument ?? _activeEditorDocument();
        return new Views.EditorView { DataContext = document };
    }
}
