using MetalForge.Core.Diagnostics;
using MetalForge.Core.Projects;

namespace MetalForge.Core.Tests;

/// <summary>
/// 项目文件（metalforge.json）与文件树的测试。
///
/// 重点覆盖"坏输入"路径：项目文件是用户会手工编辑的文本，
/// 语法错误、字段缺失、目录不存在都必须表现为可读诊断，而不是异常。
/// </summary>
public sealed class ProjectTests : IDisposable
{
    private readonly string _root;

    public ProjectTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "metalforge-project-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private string CreateProjectDirectory(string? json = null)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        if (json is not null)
        {
            File.WriteAllText(Path.Combine(directory, ProjectFile.FileName), json, System.Text.Encoding.UTF8);
        }

        return directory;
    }

    private const string MinimalProjectJson = """
        {
          "name": "test kernel",
          "architecture": "aarch64",
          "bootMethod": "u_boot",
          "sourceDirectory": "src",
          "compilerFlags": ["-ffreestanding"],
          "run": { "memoryMegabytes": 512, "gdbStub": true, "gdbPort": 1235 }
        }
        """;

    [Fact]
    public void Load_ReadsAllFields()
    {
        var directory = CreateProjectDirectory(MinimalProjectJson);

        var result = ProjectFile.Load(directory);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));
        var project = result.Project!;
        Assert.Equal("test kernel", project.Name);
        Assert.Equal("aarch64", project.Architecture);
        Assert.Equal("u_boot", project.BootMethod);
        Assert.Equal([" -ffreestanding".Trim()], project.CompilerFlags);
        Assert.Equal(512, project.Run.MemoryMegabytes);
        Assert.True(project.Run.GdbStub);
        Assert.Equal(1235, project.Run.GdbPort);
    }

    [Fact]
    public void Load_MissingFile_ReportsActionableError()
    {
        var directory = CreateProjectDirectory();

        var result = ProjectFile.Load(directory);

        Assert.False(result.Succeeded);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("MFPROJ001", diagnostic.Code);
        Assert.NotNull(diagnostic.Hint);
        Assert.Contains(ProjectFile.FileName, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MalformedJson_ReportsLineNumber()
    {
        var directory = CreateProjectDirectory("""{ "name": "broken" "architecture": "x86_64" }""");

        var result = ProjectFile.Load(directory);

        Assert.False(result.Succeeded);
        var diagnostic = result.Diagnostics.First(d => d.Code == "MFPROJ003");
        Assert.NotNull(diagnostic.Line);
        Assert.NotNull(diagnostic.Hint);
    }

    [Fact]
    public void Load_EmptyFile_IsRejected()
    {
        var directory = CreateProjectDirectory(string.Empty);

        var result = ProjectFile.Load(directory);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code is "MFPROJ003" or "MFPROJ004");
    }

    [Fact]
    public void Load_InvalidValues_AreReportedWithFieldName()
    {
        var directory = CreateProjectDirectory("""{ "name": "", "architecture": "", "bootMethod": "x" }""");

        var result = ProjectFile.Load(directory);

        Assert.False(result.Succeeded);
        var diagnostics = result.Diagnostics.Where(d => d.Code == "MFPROJ005").ToArray();
        Assert.True(diagnostics.Length >= 2, $"应报告多个字段问题，实际 {diagnostics.Length}");
        Assert.Contains(diagnostics, d => d.Message.Contains("name", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("architecture", StringComparison.Ordinal));
    }

    [Fact]
    public void RoundTrip_PreservesFieldsAndWritesUtf8WithoutBom()
    {
        var directory = CreateProjectDirectory();
        var project = new MetalForgeProject
        {
            Name = "中文项目名",
            Architecture = "riscv64",
            BootMethod = "bare_metal",
            CompilerFlags = ["-mcmodel=medany"],
            Hooks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["preBuild"] = "echo hi" },
        };

        ProjectFile.Save(directory, project);

        var bytes = File.ReadAllBytes(Path.Combine(directory, ProjectFile.FileName));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "不应写入 BOM");

        var reloaded = ProjectFile.Load(directory);
        Assert.True(reloaded.Succeeded);
        Assert.Equal("中文项目名", reloaded.Project!.Name);
        Assert.Equal("riscv64", reloaded.Project.Architecture);
        Assert.Equal("-mcmodel=medany", Assert.Single(reloaded.Project.CompilerFlags));
        Assert.Equal("echo hi", reloaded.Project.Hooks["preBuild"]);
    }

    [Fact]
    public void Exists_DetectsProjectFile()
    {
        var directory = CreateProjectDirectory(MinimalProjectJson);

        Assert.True(ProjectFile.Exists(directory));
        Assert.False(ProjectFile.Exists(_root));
    }

    // -----------------------------------------------------------------
    // 文件树
    // -----------------------------------------------------------------

    private void CreateFile(string directory, string relativePath, string content = "x")
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, System.Text.Encoding.UTF8);
    }

    [Fact]
    public void ListChildren_PutsDirectoriesFirstThenSortsByName()
    {
        var directory = CreateProjectDirectory();
        CreateFile(directory, "boot/boot.S");
        CreateFile(directory, "kernel/main.c");
        CreateFile(directory, "linker.ld");
        CreateFile(directory, "Makefile");

        var tree = new ProjectFileTree();
        var listing = tree.ListChildren(directory, directory);

        Assert.Equal(
            ["boot", "kernel", "linker.ld", "Makefile"],
            listing.Nodes.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void ListChildren_HidesBuildOutputAndVersionControl()
    {
        // build/ 可能有上万文件；把它显示出来会让项目浏览器毫无用处。
        var directory = CreateProjectDirectory();
        CreateFile(directory, "src/main.c");
        CreateFile(directory, "build/Debug/obj/main.o");
        CreateFile(directory, ".git/config");
        CreateFile(directory, "bin/tool.exe");

        var tree = new ProjectFileTree();
        var listing = tree.ListChildren(directory, directory);

        Assert.Equal(["src"], listing.Nodes.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void ListChildren_CanShowHiddenEntries()
    {
        var directory = CreateProjectDirectory();
        CreateFile(directory, "src/main.c");
        CreateFile(directory, ".gitignore", "build/");

        var tree = new ProjectFileTree(new ProjectTreeOptions { ShowHidden = true });
        var listing = tree.ListChildren(directory, directory);

        Assert.Contains(listing.Nodes, node => node.Name == ".gitignore" && node.IsHidden);
    }

    [Fact]
    public void ListChildren_MissingDirectory_ReportsWarningNotThrow()
    {
        var tree = new ProjectFileTree();

        var listing = tree.ListChildren(Path.Combine(_root, "does-not-exist"), _root);

        Assert.Empty(listing.Nodes);
        Assert.Contains(listing.Diagnostics, d => d.Code == "MFPROJ010");
    }

    [Fact]
    public void ListChildren_TruncatesHugeDirectoryAndSaysSo()
    {
        var directory = CreateProjectDirectory();
        for (var index = 0; index < 20; index++)
        {
            CreateFile(directory, $"file-{index:00}.txt");
        }

        var tree = new ProjectFileTree(new ProjectTreeOptions { MaxEntriesPerDirectory = 5 });
        var listing = tree.ListChildren(directory, directory);

        Assert.Equal(5, listing.Nodes.Count);
        Assert.Contains(listing.Diagnostics, d => d.Code == "MFPROJ011");
    }

    [Fact]
    public void ListChildren_ReportsRelativePathsAndSizes()
    {
        var directory = CreateProjectDirectory();
        const string content = "int main(void) { return 0; }";
        CreateFile(directory, "src/main.c", content);

        var tree = new ProjectFileTree();
        var sourceNode = tree.ListChildren(directory, directory).Nodes.Single(n => n.IsDirectory);
        var fileNode = tree.ListChildren(sourceNode.FullPath, directory).Nodes.Single();

        Assert.Equal("src/main.c", fileNode.RelativePath);
        Assert.Equal(ProjectNodeKind.File, fileNode.Kind);

        // 注意：File.WriteAllText 默认会写入 UTF-8 BOM（3 字节），
        // 因此断言要用实际文件长度，而不是内容的字符数。
        var expectedBytes = new FileInfo(Path.Combine(directory, "src", "main.c")).Length;
        Assert.Equal(expectedBytes, fileNode.SizeBytes);
    }

    [Fact]
    public void CreateRoot_UsesDirectoryName()
    {
        var directory = CreateProjectDirectory();

        var root = ProjectFileTree.CreateRoot(directory);

        Assert.Equal(Path.GetFileName(directory), root.Name);
        Assert.Equal(string.Empty, root.RelativePath);
        Assert.True(root.IsDirectory);
    }

    [Fact]
    public void Measure_CountsFilesAndBytesRecursively()
    {
        var directory = CreateProjectDirectory();
        CreateFile(directory, "src/a.c", "aaaa");
        CreateFile(directory, "src/b.c", "bb");
        CreateFile(directory, "include/c.h", "ccc");

        var (fileCount, totalBytes) = new ProjectFileTree().Measure(directory, directory);

        Assert.Equal(3, fileCount);

        var expectedBytes =
            new FileInfo(Path.Combine(directory, "src", "a.c")).Length +
            new FileInfo(Path.Combine(directory, "src", "b.c")).Length +
            new FileInfo(Path.Combine(directory, "include", "c.h")).Length;

        Assert.Equal(expectedBytes, totalBytes);
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
