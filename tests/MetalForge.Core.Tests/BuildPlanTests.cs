using MetalForge.Core.Build;
using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;
using MetalForge.Core.Projects;
using MetalForge.Core.Workspace;

namespace MetalForge.Core.Tests;

/// <summary>
/// 构建计划与 CMake 工程生成的测试。
///
/// 本机实测**没有任何 OSDev 工具链**，因此这里刻意把"能验证的部分"与
/// "需要工具才能验证的部分"分开：构建方案的生成、必需选项、路径引用、
/// 可复现性全部可测；真正调用 cmake 的部分由人工验收覆盖。
/// </summary>
public sealed class BuildPlanTests : IDisposable
{
    private readonly string _root;
    private readonly AssetResolver _resolver;

    public BuildPlanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "metalforge-build-tests", Guid.NewGuid().ToString("N"));
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
        string? linkerScript = "linker.ld",
        IReadOnlyList<string>? compilerFlags = null,
        Action<string>? populate = null)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        ProjectFile.Save(directory, new MetalForgeProject
        {
            Name = "test kernel",
            Architecture = architecture,
            BootMethod = bootMethod,
            LinkerScript = linkerScript,
            CompilerFlags = compilerFlags ?? [],
        });

        // 默认放一个最小可用项目内容；populate 可以覆盖。
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        File.WriteAllText(Path.Combine(directory, "src", "main.c"), "void kmain(void) {}", System.Text.Encoding.UTF8);

        if (linkerScript is not null)
        {
            File.WriteAllText(Path.Combine(directory, linkerScript), "ENTRY(kmain)", System.Text.Encoding.UTF8);
        }

        populate?.Invoke(directory);

        using var service = new ProjectService(_resolver);
        var workspace = service.Open(directory);
        Assert.NotNull(workspace);
        return workspace!;
    }

    // -----------------------------------------------------------------
    // 语言分类
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("a.c", SourceLanguage.C)]
    [InlineData("a.cc", SourceLanguage.Cpp)]
    [InlineData("a.cpp", SourceLanguage.Cpp)]
    [InlineData("a.cxx", SourceLanguage.Cpp)]
    [InlineData("a.asm", SourceLanguage.Nasm)]
    [InlineData("a.nasm", SourceLanguage.Nasm)]
    [InlineData("a.o", SourceLanguage.PrebuiltObject)]
    [InlineData("a.a", SourceLanguage.PrebuiltObject)]
    public void ClassifyByExtension_MapsKnownExtensions(string fileName, SourceLanguage expected)
        => Assert.Equal(expected, BuildPlanFactory.ClassifyByExtension(fileName));

    [Fact]
    public void ClassifyByExtension_DistinguishesDotSFromDotS()
    {
        // 大小写敏感：.S 需要预处理器（可含 #include），.s 不需要。
        // 用不区分大小写的字典会把两者混为一谈，生成的工程就会用错工具链路径。
        Assert.Equal(SourceLanguage.AssemblerWithPreprocessor, BuildPlanFactory.ClassifyByExtension("boot.S"));
        Assert.Equal(SourceLanguage.Assembler, BuildPlanFactory.ClassifyByExtension("boot.s"));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.md")]
    [InlineData("Makefile")]
    [InlineData("linker.ld")]
    public void ClassifyByExtension_RejectsNonSources(string fileName)
        => Assert.Null(BuildPlanFactory.ClassifyByExtension(fileName));

    // -----------------------------------------------------------------
    // 构建方案
    // -----------------------------------------------------------------

    [Fact]
    public void Create_CollectsSourcesAndIncludes()
    {
        var workspace = CreateWorkspace(populate: directory =>
        {
            Directory.CreateDirectory(Path.Combine(directory, "include"));
            File.WriteAllText(Path.Combine(directory, "include", "kernel.h"), "#pragma once", System.Text.Encoding.UTF8);
            File.WriteAllText(Path.Combine(directory, "src", "boot.S"), "nop", System.Text.Encoding.UTF8);
            File.WriteAllText(Path.Combine(directory, "src", "extra.asm"), "nop", System.Text.Encoding.UTF8);
        });

        var plan = BuildPlanFactory.Create(workspace);

        Assert.True(plan.IsUsable, string.Join(" | ", plan.Diagnostics.Select(d => d.ToString())));
        Assert.Equal(3, plan.Sources.Count);
        Assert.Contains(plan.Sources, source => source.Language == SourceLanguage.C);
        Assert.Contains(plan.Sources, source => source.Language == SourceLanguage.AssemblerWithPreprocessor);
        Assert.Contains(plan.Sources, source => source.Language == SourceLanguage.Nasm);
        Assert.Single(plan.IncludeDirectories);
    }

    [Fact]
    public void Create_SkipsBuildOutputDirectories()
    {
        // 关键：不跳过的话第二次构建会把上一次的产物当输入，出现"改了源码没变化"。
        var workspace = CreateWorkspace(populate: directory =>
        {
            Directory.CreateDirectory(Path.Combine(directory, "src", "build"));
            File.WriteAllText(
                Path.Combine(directory, "src", "build", "stale.c"),
                "int stale;",
                System.Text.Encoding.UTF8);
        });

        var plan = BuildPlanFactory.Create(workspace);

        Assert.DoesNotContain(plan.Sources, source => source.RelativePath.Contains("stale", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_ReportsMissingLinkerScript()
    {
        // 先建出合法项目，再删掉链接脚本：这模拟的是"文件被移动/删除后重新构建"，
        // 而不是"配置里写了个不存在的名字"（后者在打开项目时就会被矩阵校验拦下）。
        var workspace = CreateWorkspace(linkerScript: "linker.ld");
        File.Delete(Path.Combine(workspace.RootDirectory, "linker.ld"));

        var plan = BuildPlanFactory.Create(workspace);

        Assert.Contains(plan.Diagnostics, d => d.Code == "MFBUILD003");
        Assert.False(plan.IsUsable);
        Assert.Null(plan.LinkerScript);
    }

    [Fact]
    public void Create_ReportsMissingSourceDirectory()
    {
        var workspace = CreateWorkspace(populate: directory =>
            Directory.Delete(Path.Combine(directory, "src"), recursive: true));

        var plan = BuildPlanFactory.Create(workspace);

        Assert.Contains(plan.Diagnostics, d => d.Code == "MFBUILD004");
        Assert.Contains(plan.Diagnostics, d => d.Code == "MFBUILD002");
        Assert.False(plan.IsUsable);
    }

    [Fact]
    public void Create_SortsSourcesForReproducibleOutput()
    {
        var workspace = CreateWorkspace(populate: directory =>
        {
            File.WriteAllText(Path.Combine(directory, "src", "zeta.c"), "int z;", System.Text.Encoding.UTF8);
            File.WriteAllText(Path.Combine(directory, "src", "alpha.c"), "int a;", System.Text.Encoding.UTF8);
        });

        var plan = BuildPlanFactory.Create(workspace);

        var paths = plan.Sources.Select(source => source.RelativePath).ToArray();
        Assert.Equal(paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), paths);
    }

    [Fact]
    public void Create_TakesRequiredFlagsFromArchitectureMatrix()
    {
        var workspace = CreateWorkspace();

        var plan = BuildPlanFactory.Create(workspace);

        // 这些不是风格偏好：漏掉会在运行时出问题，因此必须来自矩阵而不是硬编码。
        Assert.Contains("-ffreestanding", plan.RequiredCompilerFlags);
        Assert.Contains("-mno-red-zone", plan.RequiredCompilerFlags);
    }

    [Fact]
    public void Create_KeepsUserFlagsSeparateFromRequiredFlags()
    {
        // 分开保存，生成时才能把必需项排在前面（用户可覆盖取值，但漏不掉必需项）。
        var workspace = CreateWorkspace(compilerFlags: ["-O2", "-DFOO=1"]);

        var plan = BuildPlanFactory.Create(workspace);

        Assert.Equal(["-O2", "-DFOO=1"], plan.UserCompilerFlags);
        Assert.DoesNotContain("-O2", plan.RequiredCompilerFlags);
    }

    [Theory]
    [InlineData("x86_64", "multiboot2", "bootable.iso")]
    [InlineData("x86_64", "uefi", "kernel.efi")]
    [InlineData("aarch64", "u_boot", "kernel.bin")]  // u_boot 交付的是 flat binary
    [InlineData("cortex_m", "direct_bare", "firmware.elf")]
    [InlineData("i686", "bios_mbr", "disk.img")]
    public void Create_DerivesOutputFileNameFromBootMethod(
        string architecture,
        string bootMethod,
        string expectedFileName)
    {
        var workspace = CreateWorkspace(architecture: architecture, bootMethod: bootMethod);

        var plan = BuildPlanFactory.Create(workspace);

        Assert.Equal(expectedFileName, plan.OutputFileName);
    }

    [Fact]
    public void LinkerFlags_AlwaysRequestBareMetalLinking()
    {
        // 不加 -nostdlib -static 时链接器会去找 libc 与动态链接器，
        // 在裸机目标上不存在，报错信息还会指向"找不到 -lc"而不是配置问题。
        var workspace = CreateWorkspace();
        var plan = BuildPlanFactory.Create(workspace);

        var flags = plan.LinkerFlags();

        Assert.Contains("-nostdlib", flags);
        Assert.Contains("-static", flags);
        Assert.Contains("-T", flags);
        Assert.Contains(plan.LinkerScript!, flags);
    }

    [Fact]
    public void LinkerFlags_PutUserFlagsLast()
    {
        var workspace = CreateWorkspace();
        var plan = BuildPlanFactory.Create(workspace) with { UserLinkerFlags = ["--gc-sections"] };

        var flags = plan.LinkerFlags();

        Assert.Equal("--gc-sections", flags[^1]);
    }

    [Fact]
    public void SanitizeTargetName_ReplacesIllegalCharacters()
    {
        // 尾部下划线被 Trim 掉：目标名以 _ 结尾没有意义，还会与后缀拼接时产生歧义。
        Assert.Equal("MetalForge", BuildPlanFactory.SanitizeTargetName("MetalForge 示例内核"));
        Assert.Equal("my_kernel", BuildPlanFactory.SanitizeTargetName("my-kernel"));
        Assert.Equal("kernel", BuildPlanFactory.SanitizeTargetName(""));
        Assert.Equal("kernel", BuildPlanFactory.SanitizeTargetName("   "));
    }

    // -----------------------------------------------------------------
    // CMakeLists.txt 生成
    // -----------------------------------------------------------------

    [Fact]
    public void RenderManifest_RefusesToBuildWithoutToolchainFile()
    {
        var workspace = CreateWorkspace();
        var plan = BuildPlanFactory.Create(workspace);

        var manifest = CMakeProjectWriter.RenderManifest(plan);

        // 没有这条检查时，`cmake -B build` 会用宿主编译器，问题在链接阶段才暴露。
        Assert.Contains("if(NOT CMAKE_TOOLCHAIN_FILE)", manifest, StringComparison.Ordinal);
        Assert.Contains("CMAKE_TRY_COMPILE_TARGET_TYPE", CMakeProjectWriter.RenderToolchainFile(plan, "gcc.exe"), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderManifest_IncludesRequiredFlagsWithExplanations()
    {
        var workspace = CreateWorkspace();
        var plan = BuildPlanFactory.Create(workspace);

        var manifest = CMakeProjectWriter.RenderManifest(plan);

        Assert.Contains("set(MF_REQUIRED_FLAGS", manifest, StringComparison.Ordinal);
        Assert.Contains("-mno-red-zone", manifest, StringComparison.Ordinal);

        // 用户看到必需选项时应当知道为什么：生成的注释里要有用途说明。
        Assert.Contains("红色区域", manifest, StringComparison.Ordinal);
        Assert.Contains("不存在宿主运行时", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderManifest_UsesForwardSlashesAndQuotesPathsWithSpaces()
    {
        // 项目目录名带空格是常见的（"My OS Project"）。
        // 反斜杠在 CMake 里是转义字符，含空格的路径必须加引号 —— 两条任一不满足都会生成坏文件。
        var directory = Path.Combine(_root, "pro ject with spaces");
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        Directory.CreateDirectory(Path.Combine(directory, "include"));
        File.WriteAllText(Path.Combine(directory, "src", "main.c"), "void k(void){}", System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "include", "k.h"), "#pragma once", System.Text.Encoding.UTF8);
        ProjectFile.Save(directory, new MetalForgeProject { Name = "spaced", Architecture = "x86_64", BootMethod = "multiboot2" });

        using var service = new ProjectService(_resolver);
        var workspace = service.Open(directory)!;
        var plan = BuildPlanFactory.Create(workspace);

        var manifest = CMakeProjectWriter.RenderManifest(plan);

        Assert.DoesNotContain('\\', manifest);

        // 相对路径里不含空格时无需引号；含空格时必须加。
        Assert.Contains("  src/main.c", manifest, StringComparison.Ordinal);

        var toolchain = CMakeProjectWriter.RenderToolchainFile(plan, @"C:\Program Files\gcc\bin\x86_64-elf-gcc.exe");
        Assert.Contains("\"C:/Program Files/gcc/bin/x86_64-elf-gcc.exe\"", toolchain, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderManifest_IsReproducible()
    {
        // 生成的工程要能进版本控制：同样的输入必须得到逐字节相同的输出。
        var workspace = CreateWorkspace();
        var plan = BuildPlanFactory.Create(workspace);

        var first = CMakeProjectWriter.RenderManifest(plan);
        var second = CMakeProjectWriter.RenderManifest(plan);

        Assert.Equal(first, second);
    }

    [Fact]
    public void RenderManifest_DeclaresNasmCustomCommandsOnlyWhenNeeded()
    {
        var withNasm = CreateWorkspace(populate: directory =>
            File.WriteAllText(Path.Combine(directory, "src", "boot.asm"), "bits 64", System.Text.Encoding.UTF8));

        var manifest = CMakeProjectWriter.RenderManifest(BuildPlanFactory.Create(withNasm));
        Assert.Contains("MF_NASM_EXECUTABLE", manifest, StringComparison.Ordinal);
        Assert.Contains("-f elf64", manifest, StringComparison.Ordinal);

        var withoutNasm = CreateWorkspace();
        var plain = CMakeProjectWriter.RenderManifest(BuildPlanFactory.Create(withoutNasm));
        Assert.DoesNotContain("MF_NASM_EXECUTABLE", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderManifest_SelectsNasmFormatFromArchitecture()
    {
        // 写死 elf64 会让 32 位目标在汇编阶段就失败。
        var i686 = CreateWorkspace(architecture: "i686", bootMethod: "multiboot", populate: directory =>
            File.WriteAllText(Path.Combine(directory, "src", "boot.asm"), "bits 32", System.Text.Encoding.UTF8));

        var manifest = CMakeProjectWriter.RenderManifest(BuildPlanFactory.Create(i686));

        Assert.Contains("-f elf32", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolchainFile_DeclaresGenericBareMetalTarget()
    {
        var workspace = CreateWorkspace();
        var plan = BuildPlanFactory.Create(workspace);

        var toolchain = CMakeProjectWriter.RenderToolchainFile(plan, @"C:\tools\gcc\bin\x86_64-elf-gcc.exe");

        Assert.Contains("set(CMAKE_SYSTEM_NAME Generic)", toolchain, StringComparison.Ordinal);
        Assert.Contains("x86_64-elf-gcc.exe", toolchain, StringComparison.Ordinal);
        Assert.Contains("CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY", toolchain, StringComparison.Ordinal);

        // 工具链文件里也不允许出现反斜杠。
        Assert.DoesNotContain('\\', toolchain);
    }

    [Fact]
    public void RenderManifest_IncludesLinkerScriptDependency()
    {
        var workspace = CreateWorkspace();
        var plan = BuildPlanFactory.Create(workspace);

        var manifest = CMakeProjectWriter.RenderManifest(plan);

        // LINK_DEPENDS：改了链接脚本必须触发重新链接，否则改了内存布局却看不到效果。
        Assert.Contains("LINK_DEPENDS linker.ld", manifest, StringComparison.Ordinal);
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
