using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;
using MetalForge.Core.Targeting;

namespace MetalForge.Core.Tests;

/// <summary>
/// 目标矩阵（架构 × 引导方式）的测试。
///
/// 重点在**交叉一致性**：两个矩阵是分开维护的，
/// "架构说支持某引导方式"与"该引导方式说支持该架构"很容易只改一边，
/// 结果向导里出现选了就报错的组合。这里把它固定成会失败的测试。
/// </summary>
public sealed class TargetMatrixTests : IDisposable
{
    private readonly string _userDirectory;
    private readonly AssetResolver _resolver;

    public TargetMatrixTests()
    {
        _userDirectory = Path.Combine(Path.GetTempPath(), "metalforge-target-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_userDirectory);

        _resolver = new AssetResolver(new AssetOptions
        {
            BuiltInAssetsDirectory = TestPaths.AssetsDirectory,
            UserDirectory = _userDirectory,
        });
    }

    private TargetMatrix LoadBuiltInMatrix(out IReadOnlyList<Diagnostic> diagnostics)
    {
        var (architectureConfiguration, architectureDiagnostics) =
            _resolver.ResolveValidated("targets/architectures.json", "targets/schema/architectures.schema.json");
        var (bootConfiguration, bootDiagnostics) =
            _resolver.ResolveValidated("targets/boot-methods.json", "targets/schema/boot-methods.schema.json");

        diagnostics = [.. architectureDiagnostics, .. bootDiagnostics];

        var architectures = architectureConfiguration.Root is null
            ? []
            : TargetMatrixReader.ReadArchitectures(architectureConfiguration.Root);
        var bootMethods = bootConfiguration.Root is null
            ? []
            : TargetMatrixReader.ReadBootMethods(bootConfiguration.Root);

        return new TargetMatrix(architectures, bootMethods);
    }

    [Fact]
    public void BuiltInMatrices_PassTheirOwnSchemas()
    {
        _ = LoadBuiltInMatrix(out var diagnostics);

        var errors = diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, "目标矩阵校验失败：" + string.Join(" | ", errors.Select(d => d.ToString())));
    }

    [Fact]
    public void BuiltInMatrix_IsInternallyConsistent()
    {
        var matrix = LoadBuiltInMatrix(out _);

        var problems = matrix.Validate();

        Assert.True(problems.Count == 0, "矩阵交叉引用不一致：" + string.Join(" | ", problems));
    }

    [Fact]
    public void BuiltInMatrix_ContainsTheDocumentedArchitectureSet()
    {
        // DESIGN.md 的架构支持矩阵里列出的 8 种架构必须都在。
        var matrix = LoadBuiltInMatrix(out _);

        string[] expected = ["x86_64", "i686", "aarch64", "arm32", "riscv64", "riscv32", "cortex_m", "esp32"];

        foreach (var id in expected)
        {
            Assert.NotNull(matrix.FindArchitecture(id));
        }

        Assert.Equal(expected.Length, matrix.Architectures.Count);
    }

    [Fact]
    public void BuiltInMatrix_ContainsTheDocumentedBootMethodSet()
    {
        var matrix = LoadBuiltInMatrix(out _);

        string[] expected =
        [
            "uefi", "bios_mbr", "multiboot", "multiboot2",
            "u_boot", "coreboot", "direct_bare", "openocd_flash", "edk2_module",
        ];

        foreach (var id in expected)
        {
            Assert.NotNull(matrix.FindBootMethod(id));
        }

        Assert.Equal(expected.Length, matrix.BootMethods.Count);
    }

    [Fact]
    public void Evaluate_AcceptsDocumentedCombinations()
    {
        var matrix = LoadBuiltInMatrix(out _);

        (string Architecture, string BootMethod)[] validCombinations =
        [
            ("x86_64", "uefi"),
            ("x86_64", "multiboot2"),
            ("i686", "multiboot"),
            ("i686", "bios_mbr"),
            ("aarch64", "uefi"),
            ("aarch64", "u_boot"),
            ("cortex_m", "openocd_flash"),
            ("riscv64", "direct_bare"),
        ];

        foreach (var (architecture, bootMethod) in validCombinations)
        {
            var combination = matrix.Evaluate(architecture, bootMethod);
            Assert.True(combination.IsValid, $"{architecture} + {bootMethod} 应可用：{combination.Reason}");
        }
    }

    [Fact]
    public void Evaluate_RejectsImpossibleCombinationsWithAReason()
    {
        var matrix = LoadBuiltInMatrix(out _);

        // Cortex-M 上没有 UEFI；这类组合必须在向导里被拦住。
        var combination = matrix.Evaluate("cortex_m", "uefi");

        Assert.False(combination.IsValid);
        Assert.NotNull(combination.Reason);
        Assert.Contains("Cortex-M", combination.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_RejectsUnknownIds()
    {
        var matrix = LoadBuiltInMatrix(out _);

        Assert.False(matrix.Evaluate("not-an-arch", "uefi").IsValid);
        Assert.False(matrix.Evaluate("x86_64", "not-a-method").IsValid);
        Assert.False(matrix.Evaluate(null, null).IsValid);
    }

    [Fact]
    public void AvailableBootMethods_AlsoRequiresBidirectionalAgreement()
    {
        var matrix = LoadBuiltInMatrix(out _);
        var architecture = matrix.FindArchitecture("x86_64")!;

        var available = matrix.AvailableBootMethods(architecture);
        var ids = available.Select(method => method.Id).ToArray();

        Assert.Contains("uefi", ids);
        Assert.Contains("multiboot2", ids);
        Assert.DoesNotContain("openocd_flash", ids);
    }

    [Fact]
    public void Validate_DetectsOneSidedDeclarations()
    {
        // 构造一个"只改了一边"的矩阵，确认校验会报出来。
        var architecture = new ArchitectureDefinition
        {
            Id = "test_arch",
            DisplayName = "Test",
            TargetTriple = "test-elf",
            PreferredToolchainId = "gcc-cross",
            Bitness = 64,
            Endianness = "little",
            DefaultBootMethodId = "method_a",
            SupportedBootMethodIds = ["method_a", "method_missing"],
            QemuSystemExecutable = "qemu-system-test",
        };

        var methodA = new BootMethodDefinition
        {
            Id = "method_a",
            DisplayName = "A",
            Description = "method a description",
            ArtifactKind = "elf",
            SupportedArchitectureIds = ["test_arch"],
        };

        var methodB = new BootMethodDefinition
        {
            Id = "method_b",
            DisplayName = "B",
            Description = "method b description",
            ArtifactKind = "elf",
            SupportedArchitectureIds = ["other_arch"],
        };

        var matrix = new TargetMatrix([architecture], [methodA, methodB]);
        var problems = matrix.Validate();

        Assert.Contains(problems, p => p.Contains("method_missing", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("other_arch", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DetectsMissingDefaultBootMethod()
    {
        var architecture = new ArchitectureDefinition
        {
            Id = "test_arch",
            DisplayName = "Test",
            TargetTriple = "test-elf",
            PreferredToolchainId = "gcc-cross",
            Bitness = 32,
            Endianness = "little",
            DefaultBootMethodId = "nope",
            SupportedBootMethodIds = [],
            QemuSystemExecutable = "qemu-system-test",
        };

        var problems = new TargetMatrix([architecture], []).Validate();

        Assert.Contains(problems, p => p.Contains("defaultBootMethodId", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryArchitecture_HasATargetTripleAndRequiredFlags()
    {
        var matrix = LoadBuiltInMatrix(out _);

        foreach (var architecture in matrix.Architectures)
        {
            Assert.False(string.IsNullOrWhiteSpace(architecture.TargetTriple), $"{architecture.Id} 缺少 targetTriple");
            Assert.False(string.IsNullOrWhiteSpace(architecture.QemuSystemExecutable), $"{architecture.Id} 缺少 qemu 可执行文件");

            // 必需编译选项不能为空：这些参数漏了会在运行时出问题（例如 red zone、栈保护）。
            Assert.NotEmpty(architecture.RequiredCompilerFlags);
            Assert.Contains("-ffreestanding", architecture.RequiredCompilerFlags);

            // 每个架构都必须有可用的引导方式，否则向导里选不出东西。
            Assert.NotEmpty(matrix.AvailableBootMethods(architecture));
        }
    }

    [Fact]
    public void UefiBootMethod_DocumentsTheFirmwareAndFlagsItNeeds()
    {
        var matrix = LoadBuiltInMatrix(out _);
        var uefi = matrix.FindBootMethod("uefi")!;

        Assert.True(uefi.RequiresFirmware);
        Assert.Equal("ovmf", uefi.FirmwareKind);
        Assert.Equal("efi", uefi.ArtifactKind);

        // QEMU 参数模板必须引用固件占位符，否则 OVMF 场景下会启动到固件 shell 而不是内核。
        Assert.Contains(uefi.QemuArguments, argument => argument.Contains("{firmwareCode}", StringComparison.Ordinal));
        Assert.Contains(uefi.QemuArguments, argument => argument.Contains("{firmwareVarsCopy}", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_userDirectory))
            {
                Directory.Delete(_userDirectory, recursive: true);
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
