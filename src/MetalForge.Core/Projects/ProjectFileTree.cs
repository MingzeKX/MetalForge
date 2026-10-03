using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Projects;

/// <summary>文件树中的一个条目。</summary>
public enum ProjectNodeKind
{
    Directory,
    File,
}

/// <summary>
/// 文件树中的一个节点。
/// 子节点**按需加载**：OSDev 项目里 <c>build/</c> 可能有上万文件，
/// 一次性递归枚举会让"打开项目"变成几秒钟的卡顿。
/// </summary>
public sealed record ProjectNode
{
    public required string Name { get; init; }

    /// <summary>绝对路径。</summary>
    public required string FullPath { get; init; }

    public required ProjectNodeKind Kind { get; init; }

    /// <summary>文件大小（字节）；目录为 null。</summary>
    public long? SizeBytes { get; init; }

    /// <summary>是否为隐藏项（以点开头或系统标记）。</summary>
    public bool IsHidden { get; init; }

    /// <summary>目录是否含可展开的子项（用于界面上的展开箭头）。</summary>
    public bool HasChildren { get; init; }

    /// <summary>相对于项目根的路径，显示用。</summary>
    public required string RelativePath { get; init; }

    public bool IsDirectory => Kind == ProjectNodeKind.Directory;
}

/// <summary>
/// 项目文件树的枚举逻辑。
///
/// 过滤规则集中在这里，并由测试固定：
///   - 始终隐藏：<c>build</c>、<c>.git</c>、<c>bin</c>、<c>obj</c>、<c>node_modules</c>；
///   - 默认隐藏以点开头的项（可用选项打开）；
///   - 单目录条目上限，防止把巨量文件塞进界面。
/// </summary>
public sealed record ProjectTreeOptions
{
    /// <summary>永不显示的名称（不区分大小写）。</summary>
    public IReadOnlySet<string> ExcludedNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "build", ".git", ".vs", ".vscode", "bin", "obj", "node_modules", ".metalforge",
    };

    /// <summary>是否显示以点开头的项。</summary>
    public bool ShowHidden { get; init; }

    /// <summary>单个目录最多返回多少条目（超出部分被截断并报诊断）。</summary>
    public int MaxEntriesPerDirectory { get; init; } = 2000;
}

/// <summary>一次目录枚举的结果。</summary>
/// <param name="Nodes">排序后的条目（目录在前，然后按名称）。</param>
/// <param name="Diagnostics">枚举过程中的问题（无权限、超出上限等）。</param>
public sealed record ProjectDirectoryListing(IReadOnlyList<ProjectNode> Nodes, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>项目文件树访问器。</summary>
public sealed class ProjectFileTree
{
    private readonly ProjectTreeOptions _options;

    public ProjectFileTree(ProjectTreeOptions? options = null) => _options = options ?? new ProjectTreeOptions();

    /// <summary>项目根节点。不依赖实例选项，因此是静态的。</summary>
    public static ProjectNode CreateRoot(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        var full = Path.GetFullPath(projectDirectory);
        return new ProjectNode
        {
            Name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : full,
            FullPath = full,
            Kind = ProjectNodeKind.Directory,
            RelativePath = string.Empty,
            HasChildren = true,
        };
    }

    /// <summary>枚举一个目录的子项。不递归。</summary>
    public ProjectDirectoryListing ListChildren(string directory, string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        var diagnostics = new List<Diagnostic>();
        var nodes = new List<ProjectNode>();

        if (!Directory.Exists(directory))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFPROJ010",
                $"目录不存在或已被删除：{directory}",
                Hint: "刷新项目浏览器即可移除该条目。"));
            return new ProjectDirectoryListing(nodes, diagnostics);
        }

        try
        {
            var truncated = false;

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (nodes.Count >= _options.MaxEntriesPerDirectory)
                {
                    truncated = true;
                    break;
                }

                var name = Path.GetFileName(entry);

                if (_options.ExcludedNames.Contains(name))
                {
                    continue;
                }

                var isHidden = name.StartsWith('.');
                if (isHidden && !_options.ShowHidden)
                {
                    continue;
                }

                var isDirectory = Directory.Exists(entry);

                nodes.Add(new ProjectNode
                {
                    Name = name,
                    FullPath = entry,
                    Kind = isDirectory ? ProjectNodeKind.Directory : ProjectNodeKind.File,
                    SizeBytes = isDirectory ? null : TryGetSize(entry),
                    IsHidden = isHidden,
                    RelativePath = Path.GetRelativePath(projectRoot, entry).Replace('\\', '/'),
                    // 目录一律显示为可展开：真正的子项在展开时才枚举，
                    // 这里再探一次会多一次目录访问，代价大于收益。
                    HasChildren = isDirectory,
                });
            }

            if (truncated)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "MFPROJ011",
                    $"目录条目过多，已只显示前 {_options.MaxEntriesPerDirectory} 项：{directory}",
                    Hint: "在项目配置里排除该目录，或提高 projectTree.maxEntriesPerDirectory。"));
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFPROJ012",
                $"没有权限读取目录：{directory}",
                Hint: "该目录在项目浏览器中不可见。",
                Exception: exception));
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFPROJ013",
                $"读取目录失败：{exception.Message}",
                directory,
                Exception: exception));
        }

        // 目录在前、然后按名称不区分大小写排序：与主流 IDE 一致。
        nodes.Sort(static (left, right) =>
        {
            if (left.IsDirectory != right.IsDirectory)
            {
                return left.IsDirectory ? -1 : 1;
            }

            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        return new ProjectDirectoryListing(nodes, diagnostics);
    }

    /// <summary>统计某目录下的文件数与总字节数（用于状态栏/项目概览）。</summary>
    public (int FileCount, long TotalBytes) Measure(string directory, string projectRoot)
    {
        var fileCount = 0;
        var totalBytes = 0L;

        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var listing = ListChildren(current, projectRoot);

            foreach (var node in listing.Nodes)
            {
                if (node.IsDirectory)
                {
                    pending.Push(node.FullPath);
                }
                else
                {
                    fileCount++;
                    totalBytes += node.SizeBytes ?? 0;
                }
            }
        }

        return (fileCount, totalBytes);
    }

    private static long? TryGetSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 文件在枚举与读取之间被删除是常见竞态；大小未知不影响展示。
            return null;
        }
    }
}
