using Avalonia.Controls;
using MetalForge.App.ViewModels;

namespace MetalForge.App.Services;

/// <summary>
/// 标签页内容的工厂：把布局里的标签页 id 变成真实控件。
///
/// 未实现的标签页返回由 <see cref="PlaceholderViewModel"/> 驱动的占位视图，
/// 并标出它的目标里程碑。这里是"配置驱动"与"代码实现"的接缝，
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
            _ => BuildPlaceholder(descriptor),
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

    private Control BuildPlaceholder(Core.Layout.TabDescriptor descriptor) => new Views.PlaceholderView
    {
        DataContext = new PlaceholderViewModel
        {
            Title = GetTitle(descriptor),
            Message = _localize("placeholder.message"),
            PlannedMilestone = MilestoneFor(descriptor.ImplementationKey),
            Note = descriptor.Id,
        },
    };

    /// <summary>
    /// 标签页归属的里程碑，来自 DESIGN.md 的功能清单。
    /// 让占位内容能告诉用户"什么时候会有"，而不是永远一句"敬请期待"。
    /// </summary>
    private static string MilestoneFor(string implementationKey) => implementationKey switch
    {
        "projectFiles" or "projectTemplates" or "projectSymbols" => "M4",
        "inspectorProperties" or "inspectorArtifact" => "M2",
        "inspectorLayoutMap" => "M3",
        "outputBuild" or "outputProblems" or "terminalShell" or "terminalSerial" => "M2",
        "debugConsole" or "debugMonitor" => "M3",
        "debugStack" or "debugVariables" or "debugRegisters" or "debugBreakpoints" or "debugMemory" => "M3",
        "aiAgent" => "M5",
        "codeEditor" => "M1",
        _ => "M2",
    };
}
