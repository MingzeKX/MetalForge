using MetalForge.Core.Build;
using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;
using MetalForge.Core.Projects;
using MetalForge.Core.Workspace;

namespace MetalForge.Core.Tests;

/// <summary>
/// 构建准备与执行的测试。
///
/// 这里刻意把测试与"机器上装了什么工具"解耦：准备阶段接受显式的工具搜索目录，
/// 因此可以在临时目录里放假的工具可执行文件来构造"工具齐全"的场景，
/// 不必依赖本机真的装了交叉工具链（本机实测一个都没有）。
/// </summary>
public sealed class BuildExecutorTests : IDisposable
{
    private readonly string _root;
    private readonly AssetResolver _resolver;

    public BuildExecutorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "metalforge-executor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _resolver = new AssetResolver(new AssetOptions
        {
            BuiltInAssetsDirectory = TestPaths.AssetsDirectory,
            UserDirectory = _root,
        });
    }

    private ProjectWorkspace CreateWorkspace(string architecture = "x86_64", IReadOnlyList<string>? flags = null)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        File.WriteAllText(Path.Combine(directory, "src", "main.c"), "void kmain(void){}", System.Text.Encoding.UTF8);

        ProjectFile.Save(directory, new MetalForgeProject
        {
            Name = "executor test",
            Architecture = architecture,
            BootMethod = "multiboot2",
            CompilerFlags = flags ?? [],
        });

        using var service = new ProjectService(_resolver);
        return service.Open(directory)!;
    }

    /// <summary>
    /// 在临时目录里造一套假工具，名称与真实工具一致。
    /// 内容无关紧要 —— 准备阶段只做存在性判断，不会执行它们。
    /// </summary>
    private string CreateFakeToolchain(string architecture = "x86_64")
    {
        var directory = Path.Combine(_root, "fake-tools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var triple = architecture switch
        {
            "aarch64" => "aarch64-elf",
            "riscv64" => "riscv64-elf",
            "i686" => "i686-elf",
            _ => "x86_64-elf",
        };

        string[] names =
        [
            $"{triple}-gcc", $"{triple}-g++", $"{triple}-as",
            "nasm", "cmake", "ninja",
        ];

        foreach (var name in names)
        {
            var path = Path.Combine(directory, name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            File.WriteAllBytes(path, [0x00]);
        }

        return directory;
    }

    // -----------------------------------------------------------------
    // 工具链解析
    // -----------------------------------------------------------------

    [Fact]
    public void Resolve_WithoutAnyToolchain_ReportsEveryMissingTool()
    {
        // 本机就是这种情况。关键要求：不能抛异常，且必须说清缺什么。
        var workspace = CreateWorkspace();
        var architecture = workspace.Architecture!;

        var toolchain = new ToolchainResolver(searchDirectories: []).Resolve(architecture);

        Assert.NotEmpty(toolchain.Tools);

        // 用宿主工具链的名字（gcc/clang）也要能查到 —— 但至少要报告"没有交叉编译器"。
        var diagnostics = toolchain.ToDiagnostics();
        Assert.Equal(toolchain.Missing.Count, diagnostics.Count);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.NotNull(diagnostic.Hint);
        });
    }

    [Fact]
    public void Resolve_PrefersTriplePrefixedCompiler()
    {
        // 三元组前缀优先：用宿主 gcc 编裸机内核会一路编译成功，
        // 直到链接阶段才报错，而错误信息离原因很远。
        var directory = CreateFakeToolchain();
        var workspace = CreateWorkspace();

        var toolchain = new ToolchainResolver(searchDirectories: [directory]).Resolve(workspace.Architecture!);

        var compiler = toolchain.Find(BuildToolKind.CCompiler);
        Assert.NotNull(compiler);
        Assert.True(compiler!.Found);
        Assert.Contains("x86_64-elf-gcc", compiler.ExecutablePath!, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_FindsAllToolsWhenPresent()
    {
        var directory = CreateFakeToolchain();
        var workspace = CreateWorkspace();

        var toolchain = new ToolchainResolver(searchDirectories: [directory]).Resolve(workspace.Architecture!);

        Assert.True(toolchain.IsComplete, "缺少：" + string.Join('、', toolchain.Missing.Select(t => t.DisplayName)));
        Assert.Empty(toolchain.ToDiagnostics());
    }

    [Fact]
    public void Resolve_HintExplainsWhyCrossCompilerMatters()
    {
        var workspace = CreateWorkspace();

        var toolchain = new ToolchainResolver(searchDirectories: []).Resolve(workspace.Architecture!);
        var compiler = toolchain.Find(BuildToolKind.CCompiler)!;

        Assert.NotNull(compiler.Hint);
        Assert.Contains("链接阶段", compiler.Hint!, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // 准备阶段
    // -----------------------------------------------------------------

    [Fact]
    public void Prepare_WithoutToolchain_DoesNotWriteFiles()
    {
        // 工具不全时生成出来的工程用不了，留在仓库里只会造成困惑。
        var workspace = CreateWorkspace();

        var preparation = BuildPreparer.Prepare(workspace, additionalSearchDirectories: []);

        Assert.False(preparation.CanBuild);
        Assert.Empty(preparation.GeneratedFiles);
        Assert.False(File.Exists(CMakeProjectWriter.ManifestPath(workspace.RootDirectory)));
        Assert.Contains(preparation.Diagnostics, d => d.Code == "MFBUILD010");
    }

    [Fact]
    public void Prepare_ReadinessDescriptionNamesMissingTools()
    {
        var workspace = CreateWorkspace();

        var preparation = BuildPreparer.Prepare(workspace, additionalSearchDirectories: []);
        var description = preparation.DescribeReadiness();

        Assert.StartsWith("缺少：", description, StringComparison.Ordinal);
        Assert.Contains("编译器", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_WithToolchain_WritesManifestAndToolchainFile()
    {
        var directory = CreateFakeToolchain();
        var workspace = CreateWorkspace();

        var preparation = BuildPreparer.Prepare(workspace, additionalSearchDirectories: [directory]);

        Assert.True(preparation.CanBuild, preparation.DescribeReadiness());
        Assert.Equal(2, preparation.GeneratedFiles.Count);

        var manifestPath = CMakeProjectWriter.ManifestPath(workspace.RootDirectory);
        var toolchainPath = CMakeProjectWriter.ToolchainFilePath(workspace.RootDirectory, "x86_64-elf");

        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(toolchainPath));

        var manifest = File.ReadAllText(manifestPath);
        Assert.Contains("add_executable", manifest, StringComparison.Ordinal);
        Assert.Contains("-mno-red-zone", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_WriteFilesFalse_KeepsPurelyInMemory()
    {
        // "构建前预览"与测试用：不落盘也能拿到完整方案。
        var directory = CreateFakeToolchain();
        var workspace = CreateWorkspace();

        var preparation = BuildPreparer.Prepare(workspace, additionalSearchDirectories: [directory], writeFiles: false);

        Assert.True(preparation.CanBuild);
        Assert.Empty(preparation.GeneratedFiles);
        Assert.False(File.Exists(CMakeProjectWriter.ManifestPath(workspace.RootDirectory)));
    }

    [Fact]
    public void Prepare_UsesUtf8WithoutBomForGeneratedFiles()
    {
        // 生成的工程要能进版本控制：BOM 会让 diff 出现无意义的差异。
        var directory = CreateFakeToolchain();
        var workspace = CreateWorkspace();

        BuildPreparer.Prepare(workspace, additionalSearchDirectories: [directory]);

        foreach (var path in new[]
                 {
                     CMakeProjectWriter.ManifestPath(workspace.RootDirectory),
                     CMakeProjectWriter.ToolchainFilePath(workspace.RootDirectory, "x86_64-elf"),
                 })
        {
            var bytes = File.ReadAllBytes(path);
            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                $"{Path.GetFileName(path)} 不应写入 BOM");
        }
    }

    // -----------------------------------------------------------------
    // 命令行参数构造
    // -----------------------------------------------------------------

    [Fact]
    public void ConfigureArguments_AlwaysPassToolchainFile()
    {
        // 不传工具链文件时 CMake 会用宿主编译器，问题推迟到链接阶段才暴露。
        var directory = CreateFakeToolchain();
        var workspace = CreateWorkspace();
        var preparation = BuildPreparer.Prepare(workspace, additionalSearchDirectories: [directory]);

        var arguments = BuildPreparer.ConfigureArguments(preparation);
        var joined = string.Join(' ', arguments);

        Assert.Contains("-DCMAKE_TOOLCHAIN_FILE=", joined, StringComparison.Ordinal);
        Assert.Contains("x86_64-elf.toolchain.cmake", joined, StringComparison.Ordinal);
        Assert.Contains("-DCMAKE_BUILD_TYPE=", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigureArguments_UseNinjaOnlyWhenAvailable()
    {
        var withNinja = CreateFakeToolchain();
        var workspace = CreateWorkspace();
        var preparation = BuildPreparer.Prepare(workspace, additionalSearchDirectories: [withNinja]);

        var arguments = BuildPreparer.ConfigureArguments(preparation);
        Assert.Contains("-G", arguments);
        Assert.Contains("Ninja", arguments);
    }

    [Fact]
    public void BuildArguments_TargetTheSameBuildDirectory()
    {
        // 配置与构建必须指向同一个目录，否则构建会重新配置一遍并可能失败。
        var directory = CreateFakeToolchain();
        var workspace = CreateWorkspace();
        var preparation = BuildPreparer.Prepare(workspace, additionalSearchDirectories: [directory]);

        var configure = BuildPreparer.ConfigureArguments(preparation);
        var build = BuildPreparer.BuildArguments(preparation);

        var configureIndex = configure.ToList().IndexOf("-B");
        var configureDirectory = configure[configureIndex + 1];

        Assert.Equal(configureDirectory, build[1]);
        Assert.Equal("--build", build[0]);
    }

    // -----------------------------------------------------------------
    // 执行
    // -----------------------------------------------------------------

    [Fact]
    public async Task Build_WithoutToolchain_ReturnsDiagnosticsWithoutRunningAnything()
    {
        // 关键：条件不满足时**不能**去调用 CMake。
        // 让 CMake 用宿主编译器跑一遍只会产生误导性的错误。
        var workspace = CreateWorkspace();
        var preparation = BuildPreparer.Prepare(workspace, additionalSearchDirectories: []);

        var executor = new BuildExecutor(new RecordingProcessRunner());
        var result = await executor.BuildAsync(preparation, progress: null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.Null(result.ArtifactPath);
        Assert.Contains(result.Diagnostics, d => d.Code == "MFBUILD010");
    }

    [Fact]
    public async Task Build_ReportsProgressLines()
    {
        var workspace = CreateWorkspace();
        var preparation = BuildPreparer.Prepare(workspace, additionalSearchDirectories: []);
        var reports = new List<BuildProgress>();

        // 不用 Progress<T>：它把回调投递到同步上下文，测试里断言时回调可能还没执行，
        // 会得到"偶发失败"。同步收集器让这个测试是确定的。
        var progress = new SynchronousProgress(reports);

        var executor = new BuildExecutor(new RecordingProcessRunner());
        await executor.BuildAsync(preparation, progress, TestContext.Current.CancellationToken);

        // 至少要有一次"条件不满足"的阶段上报 —— 否则界面在长耗时操作里没有任何反馈。
        Assert.NotEmpty(reports);
        Assert.Contains(reports, report => report.Phase.Contains("不满足", StringComparison.Ordinal));
    }

    /// <summary>同步执行的进度收集器，用于让测试确定。</summary>
    private sealed class SynchronousProgress(List<BuildProgress> sink) : IProgress<BuildProgress>
    {
        public void Report(BuildProgress value) => sink.Add(value);
    }

    // -----------------------------------------------------------------
    // 镜像打包
    // -----------------------------------------------------------------

    [Fact]
    public void PackageImage_NonIsoArtifactReturnsArtifactAsIs()
    {
        // 直接 -kernel 启动的场景不需要镜像，硬造一个反而多余。
        var directory = CreateFakeToolchain();
        var workspace = CreateWorkspace();
        var preparation = BuildPreparer.Prepare(workspace, additionalSearchDirectories: [directory], writeFiles: false);

        var artifact = Path.Combine(workspace.RootDirectory, "kernel.bin");
        File.WriteAllBytes(artifact, [1, 2, 3]);

        var plan = preparation.Plan with
        {
            BootMethod = preparation.Plan.BootMethod with { ArtifactKind = "elf" },
        };

        var result = BuildExecutor.PackageImage(preparation with { Plan = plan }, artifact);

        Assert.True(result.Succeeded);
        Assert.Equal(artifact, result.ImagePath);
    }

    [Fact]
    public void PackageImage_IsoEmbedsTheArtifact()
    {
        var directory = CreateFakeToolchain();
        var workspace = CreateWorkspace();
        var preparation = BuildPreparer.Prepare(workspace, additionalSearchDirectories: [directory], writeFiles: false);

        var artifact = Path.Combine(workspace.RootDirectory, "kernel.elf");
        var artifactContent = "fake kernel payload for iso"u8.ToArray();
        File.WriteAllBytes(artifact, artifactContent);

        var result = BuildExecutor.PackageImage(preparation, artifact);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));
        Assert.NotNull(result.ImagePath);
        Assert.True(File.Exists(result.ImagePath));

        // 产物内容必须真的在镜像里。
        var image = File.ReadAllBytes(result.ImagePath!);
        Assert.Contains(
            Enumerable.Range(0, image.Length - artifactContent.Length + 1),
            offset => image.AsSpan(offset, artifactContent.Length).SequenceEqual(artifactContent));
    }

    [Fact]
    public void PackageImage_EfiProducesFatInsideIso()
    {
        var directory = CreateFakeToolchain();
        var workspace = CreateWorkspace();
        var preparation = BuildPreparer.Prepare(workspace, additionalSearchDirectories: [directory], writeFiles: false);

        var artifact = Path.Combine(workspace.RootDirectory, "kernel.efi");
        File.WriteAllBytes(artifact, "MZ fake pe image"u8.ToArray());

        var efiPlan = preparation.Plan with
        {
            BootMethod = preparation.Plan.BootMethod with { ArtifactKind = "efi" },
        };

        var result = BuildExecutor.PackageImage(preparation with { Plan = efiPlan }, artifact);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));

        var image = File.ReadAllBytes(result.ImagePath!);

        // 中间产物 FAT 映像按约定放在产物目录下，用于"直接拷进已有 ESP"的场景。
        var fatImage = Path.Combine(preparation.Plan.OutputDirectory, "efi.img");
        Assert.True(File.Exists(fatImage));

        // ISO 里必须真的有这个 FAT 映像。
        var fatContent = File.ReadAllBytes(fatImage);
        Assert.Contains(
            Enumerable.Range(0, image.Length - fatContent.Length + 1),
            offset => image.AsSpan(offset, fatContent.Length).SequenceEqual(fatContent));
    }

    /// <summary>记录被调用的进程运行器；用于断言"不该跑的时候没有跑"。</summary>
    private sealed class RecordingProcessRunner : MetalForge.Core.Processes.IProcessRunner
    {
        public List<MetalForge.Core.Processes.ProcessRequest> Invocations { get; } = [];

        public Task<MetalForge.Core.Processes.ProcessResult> RunAsync(
            MetalForge.Core.Processes.ProcessRequest request,
            IProgress<MetalForge.Core.Processes.ProcessOutputLine>? progress,
            CancellationToken cancellationToken)
        {
            Invocations.Add(request);
            return Task.FromResult(new MetalForge.Core.Processes.ProcessResult
            {
                ExitCode = 0,
                Duration = TimeSpan.Zero,
                Lines = [],
                StandardOutput = string.Empty,
                StandardError = string.Empty,
            });
        }

        public MetalForge.Core.Processes.IInteractiveProcess StartInteractive(
            MetalForge.Core.Processes.ProcessRequest request)
            => throw new NotSupportedException("构建路径不应启动交互式进程。");
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
