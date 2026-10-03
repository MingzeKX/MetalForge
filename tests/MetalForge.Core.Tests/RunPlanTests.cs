using MetalForge.Core.Build;
using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;
using MetalForge.Core.Projects;
using MetalForge.Core.Workspace;

namespace MetalForge.Core.Tests;

/// <summary>
/// QEMU 运行方案生成的测试。
///
/// 重点覆盖"参数缺失时必须说清楚缺什么"：本机没有任何工具链，也没有固件，
/// 因此用户最可能遇到的就是这类情况。把"不存在的路径塞进命令行"是最糟的处理方式 ——
/// QEMU 报的错会离真正原因很远（"could not load PC BIOS"而不是"你没装 OVMF"）。
/// </summary>
public sealed class RunPlanTests : IDisposable
{
    private readonly string _root;
    private readonly AssetResolver _resolver;

    public RunPlanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "metalforge-run-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _resolver = new AssetResolver(new AssetOptions
        {
            BuiltInAssetsDirectory = TestPaths.AssetsDirectory,
            UserDirectory = _root,
        });
    }

    private ProjectWorkspace CreateWorkspace(
        string architecture = "x86_64",
        string bootMethod = "multiboot2",
        RunConfiguration? run = null)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        File.WriteAllText(Path.Combine(directory, "src", "main.c"), "void kmain(void){}", System.Text.Encoding.UTF8);

        ProjectFile.Save(directory, new MetalForgeProject
        {
            Name = "run test",
            Architecture = architecture,
            BootMethod = bootMethod,
            Run = run ?? new RunConfiguration(),
        });

        using var service = new ProjectService(_resolver);
        return service.Open(directory)!;
    }

    private static BuildPlan PlanFor(ProjectWorkspace workspace) => BuildPlanFactory.Create(workspace);

    private static string? ValueAfter(IReadOnlyList<string> arguments, string option)
    {
        var index = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (string.Equals(arguments[i], option, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    [Fact]
    public void Multiboot_UsesCdromAndSerialStdio()
    {
        var workspace = CreateWorkspace();
        var plan = PlanFor(workspace);

        var runPlan = RunPlanFactory.Create(workspace, plan, @"C:\qemu\qemu-system-x86_64.exe");

        Assert.True(runPlan.IsRunnable, string.Join(" | ", runPlan.Diagnostics.Select(d => d.ToString())));
        Assert.Equal(@"C:\qemu\qemu-system-x86_64.exe", runPlan.Executable);

        var arguments = runPlan.Arguments;

        // Multiboot 的产物是 ISO，必须走 -cdrom；用 -kernel 加载 ISO 会失败。
        Assert.Contains("-cdrom", arguments);
        Assert.EndsWith(plan.OutputFileName, ValueAfter(arguments, "-cdrom")!, StringComparison.Ordinal);

        // 串口是内核日志的主要通道。
        Assert.Equal("stdio", ValueAfter(arguments, "-serial"));

        // 不接监视器：否则 QEMU 监视器会抢占同一个终端，串口输出被挤掉。
        Assert.Equal("none", ValueAfter(arguments, "-monitor"));

        // 无显示：图形窗口只会拖慢启动并挡住日志。
        Assert.Equal("none", ValueAfter(arguments, "-display"));
    }

    [Fact]
    public void Uefi_UsesPflashWithSeparateCodeAndVariables()
    {
        var workspace = CreateWorkspace(bootMethod: "uefi");
        var plan = PlanFor(workspace);

        var firmware = new FirmwarePaths(@"C:\fw\OVMF_CODE.fd", @"C:\fw\OVMF_VARS.fd", @"C:\fw\OVMF.fd");
        var runPlan = RunPlanFactory.Create(workspace, plan, "qemu-system-x86_64", firmware);

        Assert.True(runPlan.IsRunnable, string.Join(" | ", runPlan.Diagnostics.Select(d => d.ToString())));

        var joined = string.Join(' ', runPlan.Arguments);

        // UEFI 必须挂两块 pflash：只读固件 + 可写变量存储。
        Assert.Contains("if=pflash", joined, StringComparison.Ordinal);
        Assert.Contains("readonly=on", joined, StringComparison.Ordinal);
        Assert.Contains("OVMF_CODE.fd", joined, StringComparison.Ordinal);
        Assert.Contains("OVMF_VARS.fd", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void Uefi_WithoutFirmwareReportsWhatIsMissing()
    {
        // 没有固件时绝不能把不存在的路径塞进命令行：
        // QEMU 会报 "could not load PC BIOS"，离"你没装 OVMF"很远。
        var workspace = CreateWorkspace(bootMethod: "uefi");
        var plan = PlanFor(workspace);

        var runPlan = RunPlanFactory.Create(workspace, plan, "qemu-system-x86_64", new FirmwarePaths());

        Assert.False(runPlan.IsRunnable);

        // OVMF 需要两块 pflash，因此会各报一条；两条都要说清缺的是什么。
        var diagnostics = runPlan.Diagnostics.Where(d => d.Code == "MFRUN002").ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Contains("ovmf", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(diagnostic.Hint);
        });

        // 没有解析成功的参数不应出现在命令行里（否则会留下 {firmwareCode} 字面量）。
        Assert.DoesNotContain(
            runPlan.Arguments,
            argument => argument.Contains('{', StringComparison.Ordinal));
    }

    [Fact]
    public void MissingQemuReportsWhichExecutableIsNeeded()
    {
        var workspace = CreateWorkspace(architecture: "riscv64", bootMethod: "direct_bare");
        var plan = PlanFor(workspace);

        var runPlan = RunPlanFactory.Create(workspace, plan, qemuExecutable: null);

        Assert.False(runPlan.IsRunnable);
        var diagnostic = Assert.Single(runPlan.Diagnostics, d => d.Code == "MFRUN001");

        // 报的是"需要哪个可执行文件"，而不是泛泛的"QEMU 缺失"。
        Assert.Contains("qemu-system-riscv64", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotNull(diagnostic.Hint);
    }

    [Fact]
    public void DirectBare_UsesKernelOption()
    {
        var workspace = CreateWorkspace(architecture: "aarch64", bootMethod: "direct_bare");
        var plan = PlanFor(workspace);

        var runPlan = RunPlanFactory.Create(workspace, plan, "qemu-system-aarch64");

        Assert.Equal(plan.ArtifactPath(), ValueAfter(runPlan.Arguments, "-kernel"));
    }

    [Fact]
    public void MemoryIsExplicitFromProjectConfiguration()
    {
        // QEMU 默认 128MB；内核刚初始化就 triple fault 往往就是因为内存不够。
        var workspace = CreateWorkspace(run: new RunConfiguration { MemoryMegabytes = 1024 });
        var plan = PlanFor(workspace);

        var runPlan = RunPlanFactory.Create(workspace, plan, "qemu-system-x86_64");

        Assert.Equal("1024", ValueAfter(runPlan.Arguments, "-m"));
    }

    [Fact]
    public void GdbStubAddsPortAndFreeze()
    {
        var workspace = CreateWorkspace(run: new RunConfiguration
        {
            GdbStub = true,
            GdbPort = 4321,
            SerialToStdout = false,
            ExtraArguments = [],
        });

        var runPlan = RunPlanFactory.Create(workspace, PlanFor(workspace), "qemu-system-x86_64");

        Assert.Equal("tcp::4321", ValueAfter(runPlan.Arguments, "-gdb"));
        Assert.Contains("-s", runPlan.Arguments);

        // 串口没有占用终端时才冻结启动，等调试器接入 —— 否则内核会在断点设上之前跑过去。
        Assert.Contains("-S", runPlan.Arguments);

        // 反过来：串口接到 stdio 时不冻结，否则用户只看到一片空白，
        // 且终端已被串口占用，无法再交互。
        var withSerial = CreateWorkspace(run: new RunConfiguration
        {
            GdbStub = true,
            GdbPort = 4321,
            SerialToStdout = true,
        });
        var withSerialPlan = RunPlanFactory.Create(withSerial, PlanFor(withSerial), "qemu-system-x86_64");
        Assert.DoesNotContain("-S", withSerialPlan.Arguments);
    }

    [Fact]
    public void ExtraArgumentsAreAppendedLast()
    {
        // 用户在 metalforge.json 里写的参数应当能覆盖我们的默认值，
        // 因此必须排在最后（QEMU 对重复选项取最后一个）。
        var workspace = CreateWorkspace(run: new RunConfiguration
        {
            ExtraArguments = ["-display", "gtk"],
        });

        var runPlan = RunPlanFactory.Create(workspace, PlanFor(workspace), "qemu-system-x86_64");

        Assert.Equal("gtk", runPlan.Arguments[^1]);
        Assert.Equal("-display", runPlan.Arguments[^2]);

        // 用户值在后面，QEMU 取最后一个，因此用户能覆盖我们的默认值。
        Assert.Equal(2, runPlan.Arguments.Count(a => a == "-display"));
    }

    [Fact]
    public void MachineAndCpuComeFromArchitectureMatrix()
    {
        var workspace = CreateWorkspace();
        var runPlan = RunPlanFactory.Create(workspace, PlanFor(workspace), "qemu-system-x86_64");

        Assert.Equal("q35", ValueAfter(runPlan.Arguments, "-machine"));
        Assert.Equal("qemu64", ValueAfter(runPlan.Arguments, "-cpu"));
    }

    [Fact]
    public void ProjectMachineOverrideWins()
    {
        var workspace = CreateWorkspace(run: new RunConfiguration { Machine = "pc" });
        var runPlan = RunPlanFactory.Create(workspace, PlanFor(workspace), "qemu-system-x86_64");

        Assert.Equal("pc", ValueAfter(runPlan.Arguments, "-machine"));
    }

    [Fact]
    public void CommandLineTextQuotesPathsWithSpaces()
    {
        // 只用于显示与复制。含空格的路径不加引号会让用户粘贴后得到"找不到文件"。
        var workspace = CreateWorkspace();
        var runPlan = RunPlanFactory.Create(
            workspace,
            PlanFor(workspace),
            @"C:\Program Files\qemu\qemu-system-x86_64.exe");

        var text = runPlan.CommandLineText();

        Assert.Contains("\"C:\\Program Files\\qemu\\qemu-system-x86_64.exe\"", text, StringComparison.Ordinal);
        Assert.StartsWith("\"C:\\Program Files", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentsAreSeparateItemsNotOneString()
    {
        // 项目纪律：外部进程参数必须逐项传递，绝不拼字符串。
        // 拼字符串时路径里的空格会把一个参数拆成两个，且无法安全转义。
        var workspace = CreateWorkspace();
        var runPlan = RunPlanFactory.Create(workspace, PlanFor(workspace), "qemu-system-x86_64");

        Assert.All(runPlan.Arguments, argument => Assert.DoesNotContain("\"", argument, StringComparison.Ordinal));
        Assert.True(runPlan.Arguments.Count > 8);
    }

    // -----------------------------------------------------------------
    // 固件查找
    // -----------------------------------------------------------------

    [Fact]
    public void FirmwareLocator_UsesProjectLocalFirmwareFirst()
    {
        // 项目自带特定版本的 OVMF 是常见需求，必须优先于系统位置。
        var workspace = CreateWorkspace(bootMethod: "uefi");
        var firmwareDirectory = Path.Combine(workspace.RootDirectory, "firmware");
        Directory.CreateDirectory(firmwareDirectory);
        File.WriteAllBytes(Path.Combine(firmwareDirectory, "OVMF_CODE.fd"), [0x00]);
        File.WriteAllBytes(Path.Combine(firmwareDirectory, "OVMF_VARS.fd"), [0x00]);

        var paths = FirmwareLocator.Locate(workspace.BootMethod!, workspace.Project, workspace.RootDirectory);

        Assert.NotNull(paths.Code);
        Assert.EndsWith("OVMF_CODE.fd", paths.Code, StringComparison.Ordinal);
        Assert.NotNull(paths.VariablesCopy);
        Assert.EndsWith("OVMF_VARS.fd", paths.VariablesCopy, StringComparison.Ordinal);
        Assert.StartsWith(workspace.RootDirectory, paths.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void FirmwareLocator_HonoursExplicitProjectPath()
    {
        var workspace = CreateWorkspace(bootMethod: "uefi");
        var customDirectory = Path.Combine(workspace.RootDirectory, "custom");
        Directory.CreateDirectory(customDirectory);
        var customFirmware = Path.Combine(customDirectory, "my-firmware.fd");
        File.WriteAllBytes(customFirmware, [0x00]);
        File.WriteAllBytes(Path.Combine(customDirectory, "OVMF_VARS.fd"), [0x00]);

        var project = workspace.Project with
        {
            Run = workspace.Project.Run with { FirmwarePath = "custom/my-firmware.fd" },
        };

        var paths = FirmwareLocator.Locate(workspace.BootMethod!, project, workspace.RootDirectory);

        Assert.Equal(Path.GetFullPath(customFirmware), Path.GetFullPath(paths.Code!));

        // 变量存储按同名规则在固件旁边找。
        Assert.EndsWith("OVMF_VARS.fd", paths.VariablesCopy!, StringComparison.Ordinal);
    }

    [Fact]
    public void FirmwareLocator_ReturnsEmptyWhenNothingFound()
    {
        var workspace = CreateWorkspace(bootMethod: "uefi");

        var paths = FirmwareLocator.Locate(workspace.BootMethod!, workspace.Project, workspace.RootDirectory);

        // 必须是"空"，而不是指向某个不存在的路径。
        Assert.Null(paths.Code);
        Assert.Null(paths.VariablesCopy);
        Assert.Null(paths.Rom);
    }

    [Fact]
    public void FirmwareLocator_DoesNothingForMethodsWithoutFirmware()
    {
        var workspace = CreateWorkspace();

        var paths = FirmwareLocator.Locate(workspace.BootMethod!, workspace.Project, workspace.RootDirectory);

        Assert.Null(paths.Code);
        Assert.Null(paths.Rom);
        Assert.Null(paths.Bootloader);
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

/// <summary>测试用的扩展：构建方案里的产物完整路径。</summary>
internal static class BuildPlanTestExtensions
{
    public static string ArtifactPath(this BuildPlan plan)
        => Path.Combine(plan.OutputDirectory, plan.OutputFileName);
}
