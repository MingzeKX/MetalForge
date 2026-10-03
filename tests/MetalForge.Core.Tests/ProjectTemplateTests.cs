using MetalForge.Core.Build;
using MetalForge.Core.Configuration;
using MetalForge.Core.Projects;
using MetalForge.Core.Targeting;
using MetalForge.Core.Templates;
using MetalForge.Core.Workspace;

namespace MetalForge.Core.Tests;

/// <summary>
/// 项目模板与脚手架的测试。
///
/// 最重要的一条：**生成的代码里不能残留占位符**。
/// 模板用 @@NAME@@ 形式替换，漏一个就会生成 <c>@@ARCH_TRIPLE@@</c>
/// 这样的字面量待在链接脚本或 README 里 —— 而且不会有任何报错，
/// 用户要构建失败之后才会发现。
///
/// 第二条：生成的项目必须能被构建系统真的识别（找到源文件、认出链接脚本）。
/// 模板生成了一个"看起来像项目"但构建不了的目录，比不生成更糟。
/// </summary>
public sealed class ProjectTemplateTests : IDisposable
{
    private readonly string _root;
    private readonly AssetResolver _resolver;

    public ProjectTemplateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "metalforge-template-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _resolver = new AssetResolver(new AssetOptions
        {
            BuiltInAssetsDirectory = TestPaths.AssetsDirectory,
            UserDirectory = _root,
        });
    }

    private string NewDirectory() => Path.Combine(_root, Guid.NewGuid().ToString("N"));

    private ArchitectureDefinition Architecture(string id)
    {
        using var service = new ProjectService(_resolver);
        var architecture = service.Targets.FindArchitecture(id);

        Assert.NotNull(architecture);
        return architecture!;
    }

    /// <summary>所有模板 × 所有适用架构。</summary>
    public static TheoryData<string, string> TemplateAndArchitectureCases()
    {
        var data = new TheoryData<string, string>();

        foreach (var template in ProjectTemplateCatalog.All)
        {
            var architectures = template.SupportedArchitectureIds.Count > 0
                ? template.SupportedArchitectureIds
                : ["x86_64", "aarch64", "riscv64", "cortex_m"];

            foreach (var architecture in architectures)
            {
                data.Add(template.Id, architecture);
            }
        }

        return data;
    }

    // -----------------------------------------------------------------
    // 目录与文件
    // -----------------------------------------------------------------

    [Fact]
    public void Create_WritesExpectedFiles()
    {
        var directory = NewDirectory();
        var template = ProjectTemplateCatalog.Find("multiboot2-c")!;

        var result = ProjectScaffolder.Create(directory, template, Architecture("x86_64"), "my kernel");

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));

        // 项目文件、链接脚本、汇编入口、C 入口都必须存在。
        Assert.Contains(ProjectFile.FileName, result.FilesCreated);
        Assert.Contains("linker.ld", result.FilesCreated);
        Assert.Contains("src/boot/boot.S", result.FilesCreated);
        Assert.Contains("src/kernel/main.c", result.FilesCreated);
        Assert.Contains("README.md", result.FilesCreated);
    }

    [Fact]
    public void Create_RefusesNonEmptyDirectoryByDefault()
    {
        // 模板展开会写多个文件，静默覆盖用户已有内容是不可接受的。
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "important.txt"), "别删我");

        var result = ProjectScaffolder.Create(
            directory,
            ProjectTemplateCatalog.Find("multiboot2-c")!,
            Architecture("x86_64"),
            "my kernel");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "MFTEMPLATE001");
        Assert.True(File.Exists(Path.Combine(directory, "important.txt")));
    }

    [Fact]
    public void Create_OverwriteFlagAllowsNonEmptyDirectory()
    {
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "existing.txt"), "x");

        var result = ProjectScaffolder.Create(
            directory,
            ProjectTemplateCatalog.Find("multiboot2-c")!,
            Architecture("x86_64"),
            "my kernel",
            overwrite: true);

        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));
        Assert.True(File.Exists(Path.Combine(directory, "existing.txt")));
    }

    [Fact]
    public void Create_WritesUtf8WithoutBom()
    {
        // 生成的项目要能进版本控制：BOM 会让 diff 出现无意义差异。
        var directory = NewDirectory();
        ProjectScaffolder.Create(
            directory,
            ProjectTemplateCatalog.Find("multiboot2-c")!,
            Architecture("x86_64"),
            "my kernel");

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                $"{Path.GetFileName(file)} 不应写入 BOM");
        }
    }

    // -----------------------------------------------------------------
    // 占位符必须全部被替换
    // -----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(TemplateAndArchitectureCases))]
    public void GeneratedContent_HasNoLeftoverPlaceholders(string templateId, string architectureId)
    {
        var directory = NewDirectory();
        var template = ProjectTemplateCatalog.Find(templateId)!;

        var result = ProjectScaffolder.Create(directory, template, Architecture(architectureId), "sample kernel");
        Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(d => d.ToString())));

        var leftovers = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);

            // 占位符形如 @@NAME@@。留在生成的文件里说明替换表漏了一项。
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(content, "@@[A-Z_]+@@"))
            {
                leftovers.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        Assert.True(
            leftovers.Count == 0,
            "生成的文件里残留了未替换的占位符：" + string.Join("、", leftovers.Distinct()));
    }

    [Fact]
    public void GeneratedContent_SubstitutesArchitectureSpecificValues()
    {
        var directory = NewDirectory();
        ProjectScaffolder.Create(
            directory,
            ProjectTemplateCatalog.Find("bare-metal-c")!,
            Architecture("cortex_m"),
            "mcu kernel");

        var boot = File.ReadAllText(Path.Combine(directory, "src", "boot", "boot.S"));
        var linker = File.ReadAllText(Path.Combine(directory, "linker.ld"));

        // Cortex-M 用 ldr 装载栈指针，而不是 x86 的 movq。
        Assert.Contains("ldr sp, =stack_top", boot, StringComparison.Ordinal);

        // Cortex-M 的加载地址是 Flash 起始处，不是 x86 的 1MB。
        Assert.Contains("0x00000000", linker, StringComparison.Ordinal);
        Assert.DoesNotContain("1M", linker, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedContent_UsesArchitectureLoadAddress()
    {
        var x86 = NewDirectory();
        ProjectScaffolder.Create(
            x86,
            ProjectTemplateCatalog.Find("bare-metal-c")!,
            Architecture("x86_64"),
            "pc kernel");

        var linker = File.ReadAllText(Path.Combine(x86, "linker.ld"));
        Assert.Contains("1M", linker, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------
    // 生成的项目必须真的能被构建
    // -----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(TemplateAndArchitectureCases))]
    public void GeneratedProject_IsRecognizedByTheBuildSystem(string templateId, string architectureId)
    {
        var directory = NewDirectory();
        var template = ProjectTemplateCatalog.Find(templateId)!;
        ProjectScaffolder.Create(directory, template, Architecture(architectureId), "sample kernel");

        using var service = new ProjectService(_resolver);
        var workspace = service.Open(directory);

        Assert.NotNull(workspace);

        var plan = BuildPlanFactory.Create(workspace!);

        // 这四条合起来说明"生成的项目是一个真的项目"：
        // 构建系统找到了源文件、认出了链接脚本、没有报错。
        Assert.True(plan.IsUsable, string.Join(" | ", plan.Diagnostics.Select(d => d.ToString())));
        Assert.NotEmpty(plan.Sources);
        Assert.NotNull(plan.LinkerScript);
        Assert.Empty(plan.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(TemplateAndArchitectureCases))]
    public void GeneratedProject_TargetCombinationIsValid(string templateId, string architectureId)
    {
        // 模板声明的引导方式必须在目标矩阵里对该架构有效，
        // 否则向导里能选出来但一打开就报错。
        var directory = NewDirectory();
        ProjectScaffolder.Create(
            directory,
            ProjectTemplateCatalog.Find(templateId)!,
            Architecture(architectureId),
            "sample kernel");

        using var service = new ProjectService(_resolver);
        var workspace = service.Open(directory)!;

        Assert.True(
            workspace.TargetCombination.IsValid,
            $"模板 {templateId} + {architectureId} 的目标组合无效：{workspace.TargetCombination.Reason}");
    }

    [Fact]
    public void GeneratedProject_HasCmakeManifestRule_WhenPlanIsRendered()
    {
        // 模板生成的项目应当能生成一份合理的 CMakeLists：
        // 这一步会把源文件分类、必需选项、链接脚本全部串起来。
        var directory = NewDirectory();
        ProjectScaffolder.Create(
            directory,
            ProjectTemplateCatalog.Find("multiboot2-c")!,
            Architecture("x86_64"),
            "sample kernel");

        using var service = new ProjectService(_resolver);
        var plan = BuildPlanFactory.Create(service.Open(directory)!);

        var manifest = CMakeProjectWriter.RenderManifest(plan);

        Assert.Contains("src/boot/boot.S", manifest, StringComparison.Ordinal);
        Assert.Contains("src/kernel/main.c", manifest, StringComparison.Ordinal);
        Assert.Contains("linker.ld", manifest, StringComparison.Ordinal);

        // 汇编源必须走 ASM 列表，而不是被当成 C 文件。
        Assert.Contains("MF_ASM_SOURCES", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedNasmProject_DeclaresNasmSources()
    {
        var directory = NewDirectory();
        ProjectScaffolder.Create(
            directory,
            ProjectTemplateCatalog.Find("multiboot2-asm")!,
            Architecture("x86_64"),
            "asm kernel");

        using var service = new ProjectService(_resolver);
        var plan = BuildPlanFactory.Create(service.Open(directory)!);

        Assert.True(plan.HasNasmSources);
        Assert.DoesNotContain(plan.Sources, source => source.Language == SourceLanguage.C);
    }

    // -----------------------------------------------------------------
    // 模板目录
    // -----------------------------------------------------------------

    [Fact]
    public void Catalog_OffersDocumentedTemplates()
    {
        string[] expected = ["multiboot2-c", "uefi-c", "bare-metal-c", "multiboot2-asm"];

        foreach (var id in expected)
        {
            Assert.NotNull(ProjectTemplateCatalog.Find(id));
        }

        Assert.Equal(expected.Length, ProjectTemplateCatalog.All.Count);
    }

    [Fact]
    public void Catalog_EveryTemplateDeclaresADocumentedBootMethod()
    {
        using var service = new ProjectService(_resolver);

        foreach (var template in ProjectTemplateCatalog.All)
        {
            Assert.NotNull(service.Targets.FindBootMethod(template.BootMethodId));
        }
    }

    [Fact]
    public void Catalog_AvailableForFiltersByArchitectureAndBootMethod()
    {
        // Cortex-M 上没有 UEFI，因此 UEFI 模板不该出现在它的候选里。
        var forCortexM = ProjectTemplateCatalog.AvailableFor("cortex_m", "direct_bare");
        Assert.Contains(forCortexM, template => template.Id == "bare-metal-c");
        Assert.DoesNotContain(forCortexM, template => template.Id == "uefi-c");

        // 反过来，UEFI 场景下裸机模板不该出现。
        var forUefi = ProjectTemplateCatalog.AvailableFor("x86_64", "uefi");
        Assert.Contains(forUefi, template => template.Id == "uefi-c");
        Assert.DoesNotContain(forUefi, template => template.Id == "bare-metal-c");
    }

    [Fact]
    public void Catalog_UefiTemplateSupportsOnlyEfiCapableArchitectures()
    {
        var uefi = ProjectTemplateCatalog.Find("uefi-c")!;

        Assert.Contains("x86_64", uefi.SupportedArchitectureIds);
        Assert.Contains("aarch64", uefi.SupportedArchitectureIds);
        Assert.DoesNotContain("cortex_m", uefi.SupportedArchitectureIds);
    }

    [Fact]
    public void GeneratedUefiProject_UsesArchitectureAppropriateBootFileName()
    {
        var directory = NewDirectory();
        ProjectScaffolder.Create(
            directory,
            ProjectTemplateCatalog.Find("uefi-c")!,
            Architecture("aarch64"),
            "arm uefi");

        var readme = File.ReadAllText(Path.Combine(directory, "README.md"));

        // AArch64 的约定名是 BOOTAA64.EFI；用错名字时固件会安静地跳过该启动项。
        Assert.Contains("BOOTAA64.EFI", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("BOOTX64.EFI", readme, StringComparison.Ordinal);
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
