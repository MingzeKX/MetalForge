using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetalForge.Core.Projects;

namespace MetalForge.App.ViewModels;

/// <summary>
/// 项目浏览器里的一个节点。
///
/// 与 Core 的 <see cref="ProjectNode"/> 分开：Core 那份是不可变的枚举结果，
/// 这份承载界面状态（是否展开、是否已加载子项）。分开之后 Core 不依赖任何 UI 概念，
/// 而"展开时才枚举子目录"这件事有地方可放 ——
/// 打开项目时不递归整棵树，是因为 build/ 可能有上万文件。
/// </summary>
public sealed partial class ProjectTreeNodeViewModel : ObservableObject
{
    private readonly Func<ProjectTreeNodeViewModel, IReadOnlyList<ProjectTreeNodeViewModel>>? _childrenLoader;
    private bool _childrenLoaded;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public ProjectTreeNodeViewModel(
        ProjectNode node,
        Func<ProjectTreeNodeViewModel, IReadOnlyList<ProjectTreeNodeViewModel>>? childrenLoader = null,
        bool loadChildrenNow = false,
        bool isExpanded = false)
    {
        ArgumentNullException.ThrowIfNull(node);

        Name = node.Name;
        FullPath = node.FullPath;
        RelativePath = node.RelativePath;
        IsDirectory = node.IsDirectory;
        SizeBytes = node.SizeBytes;
        IsHidden = node.IsHidden;
        HasUnloadedChildren = node.IsDirectory && (node.HasChildren || node.IsTruncated);

        _childrenLoader = childrenLoader;
        _isExpanded = isExpanded;

        if (loadChildrenNow && childrenLoader is not null)
        {
            LoadChildren();
        }
    }

    public string Name { get; }

    public string FullPath { get; }

    public string RelativePath { get; }

    public bool IsDirectory { get; }

    public long? SizeBytes { get; }

    public bool IsHidden { get; }

    /// <summary>目录仍可能有未加载的子项（界面上显示展开箭头）。</summary>
    [ObservableProperty]
    private bool _hasUnloadedChildren;

    /// <summary>子节点。目录在首次展开时才填充。</summary>
    public ObservableCollection<ProjectTreeNodeViewModel> Children { get; } = [];

    /// <summary>供界面显示的尺寸文本。</summary>
    public string SizeText => SizeBytes switch
    {
        null => string.Empty,
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        _ => $"{SizeBytes / (1024.0 * 1024.0):0.#} MB",
    };

    /// <summary>确保子项已加载（幂等）。展开时与"全部展开"时调用。</summary>
    public void LoadChildren()
    {
        if (_childrenLoaded || _childrenLoader is null || !IsDirectory)
        {
            return;
        }

        _childrenLoaded = true;

        foreach (var child in _childrenLoader(this))
        {
            Children.Add(child);
        }

        HasUnloadedChildren = false;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            LoadChildren();
        }
    }
}

/// <summary>项目浏览器（左侧面板）的 ViewModel。</summary>
public sealed partial class ProjectExplorerViewModel : ObservableObject
{
    private readonly Core.Workspace.IProjectService _projectService;
    private readonly Func<string, string> _localize;

    [ObservableProperty]
    private string _headline = string.Empty;

    /// <summary>尚未打开项目时的引导文字。</summary>
    [ObservableProperty]
    private string _emptyMessage = string.Empty;

    [ObservableProperty]
    private bool _hasProject;

    /// <summary>当前目标的描述（架构 · 引导方式 · 配置）。</summary>
    [ObservableProperty]
    private string _targetSummary = string.Empty;

    [ObservableProperty]
    private string _projectSummary = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ProjectTreeNodeViewModel> _rootNodes = [];

    /// <summary>打开项目时遇到的问题（可读诊断）。</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _problems = [];

    /// <summary>用户双击文件时触发；宿主负责在编辑器里打开。</summary>
    public event EventHandler<string>? FileActivated;

    public ProjectExplorerViewModel(Core.Workspace.IProjectService projectService, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(projectService);
        ArgumentNullException.ThrowIfNull(localize);

        _projectService = projectService;
        _localize = localize;

        _projectService.ProjectChanged += (_, _) => Refresh();
    }

    /// <summary>重新读取当前项目并重建树。</summary>
    public void Refresh()
    {
        Headline = _localize("explorer.title");
        EmptyMessage = _localize("explorer.emptyHint");

        var workspace = _projectService.Current;
        if (workspace is null)
        {
            HasProject = false;
            RootNodes = [];
            TargetSummary = string.Empty;
            ProjectSummary = string.Empty;

            var failures = _projectService is Core.Workspace.ProjectService concrete
                ? concrete.FailureDiagnostics
                : [];
            Problems = [.. failures.Select(d => d.ToString())];
            return;
        }

        HasProject = true;
        TargetSummary = workspace.DescribeTarget();

        // 根节点显示项目名（而不是磁盘目录名）：用户认的是项目，不是它恰好放在哪个文件夹里。
        var rootNode = workspace.RootNode with { Name = workspace.Project.Name };

        var root = new ProjectTreeNodeViewModel(
            rootNode,
            childrenLoader: LoadChildren,
            loadChildrenNow: true,
            isExpanded: true);

        RootNodes = [root];

        // 项目摘要：文件数与总字节数。Measure 会走一遍文件树，
        // 因此只在打开项目时算一次，不在每次刷新时算。
        var (fileCount, totalBytes) = new ProjectFileTree().Measure(workspace.RootDirectory, workspace.RootDirectory);
        ProjectSummary = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            _localize("explorer.summary"),
            fileCount,
            totalBytes / 1024.0 / 1024.0);

        Problems =
        [
            .. workspace.Diagnostics
                .Where(d => d.Severity >= Core.Diagnostics.DiagnosticSeverity.Warning)
                .Select(d => d.ToString()),
        ];
    }

    /// <summary>刷新按钮。</summary>
    [RelayCommand]
    public void Reload() => _projectService.Refresh();

    /// <summary>在编辑器里打开一个节点（双击文件）。</summary>
    [RelayCommand]
    public void ActivateNode(ProjectTreeNodeViewModel? node)
    {
        if (node is null || node.IsDirectory)
        {
            return;
        }

        FileActivated?.Invoke(this, node.FullPath);
    }

    /// <summary>展开/收起全部（只对已加载的层级生效，避免一次枚举整个磁盘）。</summary>
    [RelayCommand]
    public void ExpandAll()
    {
        foreach (var node in RootNodes)
        {
            ExpandRecursive(node, expand: true);
        }
    }

    [RelayCommand]
    public void CollapseAll()
    {
        foreach (var node in RootNodes)
        {
            ExpandRecursive(node, expand: false);
        }
    }

    private static void ExpandRecursive(ProjectTreeNodeViewModel node, bool expand)
    {
        if (!node.IsDirectory)
        {
            return;
        }

        node.IsExpanded = expand;

        if (expand)
        {
            node.LoadChildren();
        }

        foreach (var child in node.Children)
        {
            ExpandRecursive(child, expand);
        }
    }

    private IReadOnlyList<ProjectTreeNodeViewModel> LoadChildren(ProjectTreeNodeViewModel parent)
    {
        if (_projectService is not Core.Workspace.ProjectService concrete)
        {
            return [];
        }

        var listing = concrete.ListChildren(parent.FullPath);

        return
        [
            .. listing.Nodes.Select(node => new ProjectTreeNodeViewModel(
                node,
                childrenLoader: LoadChildren,
                loadChildrenNow: false)),
        ];
    }
}
