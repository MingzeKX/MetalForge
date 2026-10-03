using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;
using MetalForge.Core.Projects;
using MetalForge.Core.Targeting;

namespace MetalForge.Core.Workspace;

/// <summary>
/// 当前打开的项目的运行时状态。
/// </summary>
public sealed class ProjectWorkspace
{
    public ProjectWorkspace(
        string rootDirectory,
        MetalForgeProject project,
        ProjectNode rootNode,
        TargetMatrix targets,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        RootDirectory = rootDirectory;
        Project = project;
        RootNode = rootNode;
        Targets = targets;
        Diagnostics = diagnostics;
    }

    /// <summary>项目根目录绝对路径。</summary>
    public string RootDirectory { get; }

    public MetalForgeProject Project { get; }

    /// <summary>文件树根节点。</summary>
    public ProjectNode RootNode { get; }

    /// <summary>目标矩阵（架构 × 引导方式），供向导与构建使用。</summary>
    public TargetMatrix Targets { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>当前架构定义；未在矩阵中找到时为 null。</summary>
    public ArchitectureDefinition? Architecture => Targets.FindArchitecture(Project.Architecture);

    /// <summary>当前引导方式定义。</summary>
    public BootMethodDefinition? BootMethod => Targets.FindBootMethod(Project.BootMethod);

    /// <summary>架构 + 引导方式组合是否成立。</summary>
    public TargetCombination TargetCombination => Targets.Evaluate(Project.Architecture, Project.BootMethod);

    /// <summary>一句话描述当前目标，用于状态栏。</summary>
    public string DescribeTarget()
    {
        var architecture = Architecture?.DisplayName ?? Project.Architecture;
        var bootMethod = BootMethod?.DisplayName ?? Project.BootMethod;
        return $"{architecture} · {bootMethod} · {Project.Configuration}";
    }

    /// <summary>项目目录下的产物输出目录（相对路径）。</summary>
    public string BuildOutputRelativePath => $"build/{Project.Architecture}/{Project.Configuration}";
}

/// <summary>项目服务：打开 / 关闭项目，并把相关对象组织成一次结果。</summary>
public interface IProjectService
{
    /// <summary>当前打开的项目；未打开时为 null。</summary>
    ProjectWorkspace? Current { get; }

    /// <summary>项目打开或关闭后触发。</summary>
    event EventHandler<ProjectWorkspace?>? ProjectChanged;

    /// <summary>
    /// 打开项目。失败时返回 null 并给出诊断，不抛异常 ——
    /// "打不开项目"应该表现为一条可读说明，而不是崩溃。
    /// </summary>
    ProjectWorkspace? Open(string projectDirectory);

    /// <summary>关闭当前项目。</summary>
    void Close();

    /// <summary>重新加载当前项目（文件树刷新等）。</summary>
    ProjectWorkspace? Refresh();
}

/// <summary>
/// <see cref="IProjectService"/> 的默认实现。
///
/// 目标矩阵是全局配置（不属于某个项目），因此在构造时加载一次并复用；
/// 项目文件与文件树则随打开的目录变化。
/// </summary>
public sealed class ProjectService : IProjectService, IDisposable
{
    private readonly AssetResolver _assetResolver;
    private readonly ProjectFileTree _fileTree;
    private readonly List<Diagnostic> _catalogDiagnostics = [];

    private TargetMatrix? _targets;
    private bool _disposed;

    public ProjectService(AssetResolver assetResolver, ProjectFileTree? fileTree = null)
    {
        ArgumentNullException.ThrowIfNull(assetResolver);
        _assetResolver = assetResolver;
        _fileTree = fileTree ?? new ProjectFileTree();
    }

    public ProjectWorkspace? Current { get; private set; }

    public event EventHandler<ProjectWorkspace?>? ProjectChanged;

    /// <summary>加载全局目标矩阵（首次访问时执行）。</summary>
    public TargetMatrix Targets => _targets ??= LoadTargetMatrix();

    public ProjectWorkspace? Open(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        if (!Directory.Exists(projectDirectory))
        {
            _lastFailureDiagnostics.Clear();
            _lastFailureDiagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFWS001",
                $"目录不存在：{projectDirectory}",
                Hint: "确认路径；如果项目在移动硬盘上，请先接入设备。"));

            SetCurrent(null);
            return null;
        }

        var loadResult = ProjectFile.Load(projectDirectory);
        if (loadResult.Project is null)
        {
            _lastFailureDiagnostics.Clear();
            _lastFailureDiagnostics.AddRange(loadResult.Diagnostics);

            SetCurrent(null);
            return null;
        }

        var diagnostics = new List<Diagnostic>(loadResult.Diagnostics);
        diagnostics.AddRange(_catalogDiagnostics);

        var rootNode = ProjectFileTree.CreateRoot(projectDirectory);

        // 组合是否成立在打开时就检查：等到构建时才报"这个架构不支持这个引导方式"
        // 对用户来说太晚了。
        var combination = Targets.Evaluate(loadResult.Project.Architecture, loadResult.Project.BootMethod);
        if (!combination.IsValid)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFWS002",
                $"项目目标组合不可用：{combination.Reason}",
                loadResult.Path,
                Hint: "修改 metalforge.json 的 architecture / bootMethod，或在 assets/targets/ 中补充该组合。"));
        }

        var workspace = new ProjectWorkspace(
            Path.GetFullPath(projectDirectory),
            loadResult.Project,
            rootNode,
            Targets,
            diagnostics);

        SetCurrent(workspace);
        return workspace;
    }

    public void Close() => SetCurrent(null);

    public ProjectWorkspace? Refresh()
        => Current is null ? null : Open(Current.RootDirectory);

    /// <summary>枚举某个目录的子项（供项目浏览器使用）。</summary>
    public ProjectDirectoryListing ListChildren(string directory)
    {
        var root = Current?.RootDirectory ?? directory;
        return _fileTree.ListChildren(directory, root);
    }

    private void SetCurrent(ProjectWorkspace? workspace)
    {
        if (workspace is null && _lastFailureDiagnostics.Count > 0)
        {
            // 打开失败时保留诊断供界面显示：调用方拿到的是 null，
            // 但仍需要知道"为什么打不开"。
            FailureDiagnostics = _lastFailureDiagnostics;
        }
        else
        {
            FailureDiagnostics = workspace?.Diagnostics ?? [];
        }

        Current = workspace;
        ProjectChanged?.Invoke(this, workspace);
    }

    private readonly List<Diagnostic> _lastFailureDiagnostics = [];

    /// <summary>最近一次打开失败的诊断；打开成功时为空。</summary>
    public IReadOnlyList<Diagnostic> FailureDiagnostics { get; private set; } = [];

    private TargetMatrix LoadTargetMatrix()
    {
        var (architectureConfiguration, architectureDiagnostics) =
            _assetResolver.ResolveValidated("targets/architectures.json", "targets/schema/architectures.schema.json");
        var (bootConfiguration, bootDiagnostics) =
            _assetResolver.ResolveValidated("targets/boot-methods.json", "targets/schema/boot-methods.schema.json");

        _catalogDiagnostics.AddRange(architectureDiagnostics);
        _catalogDiagnostics.AddRange(bootDiagnostics);

        var architectures = architectureConfiguration.Root is null
            ? []
            : TargetMatrixReader.ReadArchitectures(architectureConfiguration.Root);

        var bootMethods = bootConfiguration.Root is null
            ? []
            : TargetMatrixReader.ReadBootMethods(bootConfiguration.Root);

        var matrix = new TargetMatrix(architectures, bootMethods);

        // 交叉一致性：两个矩阵分开维护，只改一边是常见错误，
        // 而它的表现是"向导里选了组合就报错"。在加载时就报出来。
        foreach (var problem in matrix.Validate())
        {
            _catalogDiagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFWS003",
                $"目标矩阵不一致：{problem}",
                "targets/architectures.json / targets/boot-methods.json",
                Hint: "两个文件必须互相声明支持；修正其中一边即可。"));
        }

        if (architectures.Count == 0 || bootMethods.Count == 0)
        {
            _catalogDiagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFWS004",
                "目标矩阵为空：assets/targets/ 下的架构或引导方式配置无法读取。",
                Hint: "新建项目向导与构建参数生成依赖它。"));
        }

        return matrix;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ProjectChanged = null;
        Current = null;
    }
}
