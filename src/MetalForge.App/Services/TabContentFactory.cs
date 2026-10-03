using Avalonia.Controls;
using MetalForge.App.ViewModels;

namespace MetalForge.App.Services;

/// <summary>
/// 标签页内容的工厂：把布局里的标签页 id 变成真实控件。
///
/// 当前条件下没有内容的面板返回空状态视图，说明这个面板用来做什么、
/// 为什么现在是空的、下一步做什么。这里是"配置驱动"与"代码实现"的接缝，
/// 因此全部判断集中在这一个类里。
/// </summary>
public sealed class TabContentFactory : ITabContentFactory
{
    private readonly Func<string, string> _localize;
    private readonly Func<WelcomeViewModel> _welcomeFactory;
    private readonly Func<ToolchainHealthViewModel> _toolchainFactory;
    private readonly Func<AboutViewModel> _aboutFactory;
    private readonly Func<SettingsViewModel> _settingsFactory;
    private readonly Func<ProjectExplorerViewModel> _explorerFactory;
    private readonly Func<EditorViewModel> _editorFactory;

    /// <summary>
    /// 按标签页 id 缓存的 **ViewModel**（不是控件）。
    ///
    /// 这里踩过一个真实的崩溃：最初缓存的是控件实例，配置热重载触发布局重建时，
    /// 新控件树想复用仍挂在旧 TabItem 上的控件，Avalonia 直接抛出
    /// "already has a visual parent"。控件属于视觉树，一个实例只能有一个父节点；
    /// 需要跨重建保留的是状态（ViewModel），不是控件本身。
    /// </summary>
    private readonly Dictionary<string, object> _viewModels = new(StringComparer.OrdinalIgnoreCase);

    public TabContentFactory(
        Func<string, string> localize,
        Func<WelcomeViewModel> welcomeFactory,
        Func<ToolchainHealthViewModel> toolchainFactory,
        Func<AboutViewModel> aboutFactory,
        Func<SettingsViewModel> settingsFactory,
        Func<ProjectExplorerViewModel> explorerFactory,
        Func<EditorViewModel> editorFactory)
    {
        ArgumentNullException.ThrowIfNull(localize);
        ArgumentNullException.ThrowIfNull(welcomeFactory);
        ArgumentNullException.ThrowIfNull(toolchainFactory);
        ArgumentNullException.ThrowIfNull(aboutFactory);
        ArgumentNullException.ThrowIfNull(settingsFactory);
        ArgumentNullException.ThrowIfNull(explorerFactory);
        ArgumentNullException.ThrowIfNull(editorFactory);

        _localize = localize;
        _welcomeFactory = welcomeFactory;
        _toolchainFactory = toolchainFactory;
        _aboutFactory = aboutFactory;
        _settingsFactory = settingsFactory;
        _explorerFactory = explorerFactory;
        _editorFactory = editorFactory;
    }

    /// <summary>已经创建过的 ViewModel 数量（诊断用）。</summary>
    public int MaterializedTabCount => _viewModels.Count;

    public string GetTitle(Core.Layout.TabDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return _localize(descriptor.LocalizationKey);
    }

    public Control? CreateContent(Core.Layout.TabDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        // 每次调用都创建新的控件实例；状态从缓存的 ViewModel 恢复。
        return descriptor.ImplementationKey switch
        {
            "welcome" => new Views.WelcomeView { DataContext = ViewModelFor(descriptor, _welcomeFactory) },
            "toolchainHealth" => new Views.ToolchainHealthView { DataContext = ViewModelFor(descriptor, _toolchainFactory) },
            "about" => new Views.AboutView { DataContext = ViewModelFor(descriptor, _aboutFactory) },
            "settings" => new Views.SettingsView { DataContext = ViewModelFor(descriptor, _settingsFactory) },
            "projectFiles" => CreateProjectExplorerView(descriptor),
            "codeEditor" => CreateEditorView(descriptor),
            _ => BuildEmptyState(descriptor),
        };
    }

    /// <summary>
    /// 项目文件浏览器。
    /// 未打开项目时显示引导文字而不是空面板 —— 空面板是"东西坏了吗"的经典来源。
    /// </summary>
    private Control CreateProjectExplorerView(Core.Layout.TabDescriptor descriptor)
    {
        var explorer = ViewModelFor(descriptor, _explorerFactory);
        explorer.Refresh();
        return new Views.ProjectExplorerView { DataContext = explorer };
    }

    /// <summary>
    /// 代码编辑器标签页。
    /// 没有打开的文件时也给出一块可用的编辑器（而不是占位视图），
    /// 否则后续"新建文件 / 打开文件"没有落点。
    /// </summary>
    private Control CreateEditorView(Core.Layout.TabDescriptor descriptor)
    {
        var editor = ViewModelFor(descriptor, _editorFactory);

        if (editor.ActiveDocument is null)
        {
            var untitled = new EditorDocumentViewModel(
                filePath: null,
                content: string.Empty,
                highlighting: null,
                localize: _localize);

            editor.Documents.Add(untitled);
            editor.ActiveDocument = untitled;
        }

        return new Views.EditorView { DataContext = editor.ActiveDocument };
    }

    /// <summary>取（或首次创建）该标签页的 ViewModel。</summary>
    private T ViewModelFor<T>(Core.Layout.TabDescriptor descriptor, Func<T> factory)
        where T : class
    {
        if (_viewModels.TryGetValue(descriptor.Id, out var cached) && cached is T typed)
        {
            return typed;
        }

        var created = factory() ?? throw new InvalidOperationException(
            $"标签页 ViewModel 工厂返回了 null（{typeof(T).Name}，标签页 {descriptor.Id}）。");

        _viewModels[descriptor.Id] = created;
        return created;
    }

    /// <summary>
    /// 当前条件下确实没有内容可显示时的空状态。
    ///
    /// 这里刻意不再写"该标签页尚未实现，归属里程碑 M?"：
    /// 那种文案只描述了我们自己的进度，没有告诉用户任何可做的事。
    /// 现在每个空状态都说明"这个面板用来做什么、为什么现在是空的、下一步做什么"。
    /// </summary>
    private Control BuildEmptyState(Core.Layout.TabDescriptor descriptor)
    {
        var panel = PanelDescription(descriptor.ImplementationKey);

        return new Views.EmptyStateView
        {
            DataContext = new EmptyStateViewModel
            {
                Title = GetTitle(descriptor),
                Description = _localize(panel.DescriptionKey),
                Reason = _localize(panel.ReasonKey),
                Suggestion = _localize(panel.SuggestionKey),
                ActionLabel = panel.ActionKey is null ? string.Empty : _localize(panel.ActionKey),
                Action = panel.ActionKey is null ? null : RequestAction(panel.ActionKey),
            },
        };
    }

    /// <summary>
    /// 每个面板的说明文案与可用动作。
    ///
    /// 用实现键而不是标签页 id 作索引：同一个面板可能出现在多个布局里，
    /// 说明文案应当跟着面板走而不是跟着位置走。
    /// </summary>
    private static (string DescriptionKey, string ReasonKey, string SuggestionKey, string? ActionKey) PanelDescription(
        string implementationKey) => implementationKey switch
    {
        "inspectorProperties" => (
            "emptyStates.inspectorProperties.description",
            "emptyStates.inspectorProperties.reason",
            "emptyStates.inspectorProperties.suggestion",
            null),

        "inspectorArtifact" => (
            "emptyStates.inspectorArtifact.description",
            "emptyStates.inspectorArtifact.reason",
            "emptyStates.inspectorArtifact.suggestion",
            null),

        "inspectorLayoutMap" => (
            "emptyStates.inspectorLayoutMap.description",
            "emptyStates.inspectorLayoutMap.reason",
            "emptyStates.inspectorLayoutMap.suggestion",
            null),

        "outputBuild" => (
            "emptyStates.outputBuild.description",
            "emptyStates.outputBuild.reason",
            "emptyStates.outputBuild.suggestion",
            null),

        "outputProblems" => (
            "emptyStates.outputProblems.description",
            "emptyStates.outputProblems.reason",
            "emptyStates.outputProblems.suggestion",
            null),

        "terminalShell" or "terminalSerial" => (
            "emptyStates.terminal.description",
            "emptyStates.terminal.reason",
            "emptyStates.terminal.suggestion",
            null),

        "debugConsole" or "debugMonitor" or "debugStack" or "debugVariables"
            or "debugRegisters" or "debugBreakpoints" or "debugMemory" => (
            "emptyStates.debug.description",
            "emptyStates.debug.reason",
            "emptyStates.debug.suggestion",
            null),

        "projectSymbols" => (
            "emptyStates.projectSymbols.description",
            "emptyStates.projectSymbols.reason",
            "emptyStates.projectSymbols.suggestion",
            null),

        "projectTemplates" => (
            "emptyStates.projectTemplates.description",
            "emptyStates.projectTemplates.reason",
            "emptyStates.projectTemplates.suggestion",
            "emptyStates.projectTemplates.action"),

        "aiAgent" => (
            "emptyStates.aiAgent.description",
            "emptyStates.aiAgent.reason",
            "emptyStates.aiAgent.suggestion",
            null),

        _ => (
            "emptyStates.generic.description",
            "emptyStates.generic.reason",
            "emptyStates.generic.suggestion",
            null),
    };

    /// <summary>面板动作：由宿主窗口接管的请求（对话框等留在视图层）。</summary>
    private Action? RequestAction(string actionKey) => actionKey switch
    {
        "emptyStates.projectTemplates.action" => () => NewProjectRequested?.Invoke(this, EventArgs.Empty),
        _ => null,
    };

    /// <summary>"新建项目"的请求事件；由窗口接管目录选择与向导。</summary>
    public event EventHandler? NewProjectRequested;
}
