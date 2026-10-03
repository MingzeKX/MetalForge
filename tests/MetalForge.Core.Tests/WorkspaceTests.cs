using MetalForge.Core.Configuration;
using MetalForge.Core.Projects;
using MetalForge.Core.Workspace;

namespace MetalForge.Core.Tests;

/// <summary>
/// 窗口状态持久化与项目服务的测试。
///
/// 窗口状态的重点在"坏数据不能拖垮启动"：这个文件在启动路径上被读取，
/// 任何未捕获的异常都会让应用打不开。项目服务的重点在"失败要有可读原因"。
/// </summary>
public sealed class WorkspaceTests : IDisposable
{
    private readonly string _root;
    private readonly AssetResolver _resolver;

    public WorkspaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "metalforge-workspace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _resolver = new AssetResolver(new AssetOptions
        {
            BuiltInAssetsDirectory = TestPaths.AssetsDirectory,
            UserDirectory = _root,
        });
    }

    // -----------------------------------------------------------------
    // 窗口状态
    // -----------------------------------------------------------------

    [Fact]
    public void WindowState_RoundTrips()
    {
        var store = new WindowStateStore(_resolver);

        store.Save(new WindowGeometry { X = 120, Y = 80, Width = 1200, Height = 800, IsMaximized = false });

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(120, loaded!.X);
        Assert.Equal(80, loaded.Y);
        Assert.Equal(1200, loaded.Width);
        Assert.Equal(800, loaded.Height);
        Assert.False(loaded.IsMaximized);
    }

    [Fact]
    public void WindowState_RemembersMaximized()
    {
        var store = new WindowStateStore(_resolver);

        store.Save(new WindowGeometry { X = 0, Y = 0, Width = 1000, Height = 640, IsMaximized = true });

        Assert.True(store.Load()!.IsMaximized);
    }

    [Fact]
    public void WindowState_NoFileMeansNoGeometry()
    {
        // 首次启动的情形：必须返回 null 而不是一个全是 0 的对象，
        // 否则窗口会被摆到 (0,0) 且尺寸为 0。
        Assert.Null(new WindowStateStore(_resolver).Load());
    }

    [Fact]
    public void WindowState_CorruptFileIsIgnoredInsteadOfThrowing()
    {
        var store = new WindowStateStore(_resolver);
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.FilePath, "{ this is not json", System.Text.Encoding.UTF8);

        // 关键：不抛异常。这个文件在启动路径上被读，
        // 抛出去就等于"应用打不开了"。
        Assert.Null(store.Load());
    }

    [Fact]
    public void WindowGeometry_RejectsUnusableBounds()
    {
        Assert.False(new WindowGeometry().HasUsableBounds);
        Assert.False(new WindowGeometry { X = 0, Y = 0, Width = 0, Height = 0 }.HasUsableBounds);
        Assert.False(new WindowGeometry { X = 0, Y = 0, Width = 100, Height = 50 }.HasUsableBounds);
        Assert.True(new WindowGeometry { X = 0, Y = 0, Width = 1000, Height = 640 }.HasUsableBounds);
    }

    [Fact]
    public void WindowGeometry_ClampsToScreenLimits()
    {
        var geometry = new WindowGeometry { X = -500, Y = -300, Width = 9000, Height = 9000 };

        var clamped = geometry.Clamp(minimumWidth: 1024, minimumHeight: 640, maximumWidth: 3840, maximumHeight: 2160);

        Assert.Equal(3840, clamped.Width);
        Assert.Equal(2160, clamped.Height);

        // 位置保持原样：负坐标在多显示器布局下是合法的。
        Assert.Equal(-500, clamped.X);
    }

    [Fact]
    public void WindowState_WritesUtf8WithoutBom()
    {
        var store = new WindowStateStore(_resolver);
        store.Save(new WindowGeometry { X = 1, Y = 2, Width = 1000, Height = 700 });

        var bytes = File.ReadAllBytes(store.FilePath);

        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "不应写入 BOM");
    }

    // -----------------------------------------------------------------
    // 项目服务
    // -----------------------------------------------------------------

    private string CreateProject(string name = "sample", string architecture = "x86_64", string bootMethod = "multiboot2")
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        ProjectFile.Save(directory, new MetalForgeProject
        {
            Name = name,
            Architecture = architecture,
            BootMethod = bootMethod,
        });

        return directory;
    }

    [Fact]
    public void ProjectService_OpensValidProject()
    {
        var directory = CreateProject();
        using var service = new ProjectService(_resolver);

        var workspace = service.Open(directory);

        Assert.NotNull(workspace);
        Assert.Equal("sample", workspace!.Project.Name);
        Assert.Equal(Path.GetFullPath(directory), workspace.RootDirectory);
        Assert.Equal("x86_64", workspace.Architecture!.Id);
        Assert.Equal("multiboot2", workspace.BootMethod!.Id);
        Assert.True(workspace.TargetCombination.IsValid);
    }

    [Fact]
    public void ProjectService_ReportsMissingDirectory()
    {
        using var service = new ProjectService(_resolver);

        var workspace = service.Open(Path.Combine(_root, "nope"));

        Assert.Null(workspace);
        Assert.Null(service.Current);
        Assert.Contains(service.FailureDiagnostics, d => d.Code == "MFWS001");
    }

    [Fact]
    public void ProjectService_ReportsInconsistentTargetCombination()
    {
        // Cortex-M 上不存在 UEFI。这个组合必须在打开项目时就被指出，
        // 而不是等到构建时才报一句难以理解的错误。
        var directory = CreateProject(architecture: "cortex_m", bootMethod: "uefi");
        using var service = new ProjectService(_resolver);

        var workspace = service.Open(directory);

        Assert.NotNull(workspace);
        Assert.Contains(workspace!.Diagnostics, d => d.Code == "MFWS002");
        Assert.False(workspace.TargetCombination.IsValid);
    }

    [Fact]
    public void ProjectService_DescribesTargetForStatusBar()
    {
        var directory = CreateProject();
        using var service = new ProjectService(_resolver);

        var workspace = service.Open(directory)!;

        Assert.Contains("x86_64", workspace.DescribeTarget(), StringComparison.Ordinal);
        Assert.Contains("Multiboot 2", workspace.DescribeTarget(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectService_RaisesEventOnOpenAndClose()
    {
        var directory = CreateProject();
        using var service = new ProjectService(_resolver);
        var events = new List<ProjectWorkspace?>();
        service.ProjectChanged += (_, workspace) => events.Add(workspace);

        service.Open(directory);
        service.Close();

        Assert.Equal(2, events.Count);
        Assert.NotNull(events[0]);
        Assert.Null(events[1]);
        Assert.Null(service.Current);
    }

    [Fact]
    public void ProjectService_LoadsTargetMatrixFromBuiltInAssets()
    {
        using var service = new ProjectService(_resolver);

        var matrix = service.Targets;

        Assert.True(matrix.Architectures.Count >= 8, $"架构数量异常：{matrix.Architectures.Count}");
        Assert.True(matrix.BootMethods.Count >= 9, $"引导方式数量异常：{matrix.BootMethods.Count}");
    }

    [Fact]
    public void ProjectService_ListsChildrenWithinProject()
    {
        var directory = CreateProject();
        File.WriteAllText(Path.Combine(directory, "Readme.md"), "hi", System.Text.Encoding.UTF8);
        using var service = new ProjectService(_resolver);

        service.Open(directory);
        var listing = service.ListChildren(directory);

        Assert.Contains(listing.Nodes, node => node.Name == "Readme.md");
        Assert.Contains(listing.Nodes, node => node.Name == ProjectFile.FileName);
    }

    [Fact]
    public void ProjectService_ReopenPicksUpNewFiles()
    {
        var directory = CreateProject();
        using var service = new ProjectService(_resolver);
        service.Open(directory);

        File.WriteAllText(Path.Combine(directory, "new.c"), "int x;", System.Text.Encoding.UTF8);
        var refreshed = service.Refresh();

        Assert.NotNull(refreshed);
        Assert.Contains(
            service.ListChildren(directory).Nodes,
            node => node.Name == "new.c");
    }

    // -----------------------------------------------------------------
    // 文件树的整树构建（项目浏览器绑定用）
    // -----------------------------------------------------------------

    [Fact]
    public void BuildTree_FillsChildrenUpToDepth()
    {
        var directory = CreateProject();
        Directory.CreateDirectory(Path.Combine(directory, "src", "kernel"));
        File.WriteAllText(Path.Combine(directory, "src", "kernel", "main.c"), "int x;", System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "src", "boot.S"), "nop", System.Text.Encoding.UTF8);

        var (root, _) = new ProjectFileTree().BuildTree(directory, directoryDepth: 2);

        // 根 → src → boot.S / kernel（kernel 到深度上限，标记未展开但条目仍在）
        var sourceNode = root.Children.Single(node => node.Name == "src");
        Assert.Contains(sourceNode.Children, node => node.Name == "boot.S");
        Assert.Contains(sourceNode.Children, node => node.Name == "kernel" && node.IsTruncated);
    }

    [Fact]
    public void BuildTree_DepthOneKeepsDirectoriesVisible()
    {
        var directory = CreateProject();
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        File.WriteAllText(Path.Combine(directory, "src", "main.c"), "int x;", System.Text.Encoding.UTF8);

        var (root, _) = new ProjectFileTree().BuildTree(directory, directoryDepth: 1);

        // 深度 1 = 根的直接子项。目录仍然出现（标记为未展开），
        // 否则用户看到的是一个空项目树的假象。
        Assert.Contains(root.Children, node => node.Name == "src" && node.IsDirectory && node.IsTruncated);
    }

    [Fact]
    public void BuildTree_IncludesFilesAtTopLevel()
    {
        var directory = CreateProject();

        var (root, _) = new ProjectFileTree().BuildTree(directory, directoryDepth: 2);

        Assert.Contains(root.Children, node => node.Name == ProjectFile.FileName && !node.IsDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
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
