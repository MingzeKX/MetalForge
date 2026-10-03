using MetalForge.Core.Diagnostics;
using MetalForge.Core.Projects;
using MetalForge.Core.Targeting;

namespace MetalForge.Core.Templates;

/// <summary>一个可由向导生成的模板。</summary>
/// <param name="Id">稳定标识。</param>
/// <param name="DisplayName">面向用户的名称。</param>
/// <param name="Description">一句话说明它生成什么、适合谁。</param>
/// <param name="BootMethodId">要求的引导方式 id。</param>
/// <param name="SupportedArchitectureIds">支持的架构；空表示全部。</param>
public sealed record ProjectTemplate(
    string Id,
    string DisplayName,
    string Description,
    string BootMethodId,
    IReadOnlyList<string> SupportedArchitectureIds);

/// <summary>生成结果。</summary>
/// <param name="Succeeded">是否成功。</param>
/// <param name="ProjectDirectory">项目目录。</param>
/// <param name="FilesCreated">相对路径列表。</param>
/// <param name="Diagnostics">过程中的诊断。</param>
public sealed record TemplateResult(
    bool Succeeded,
    string ProjectDirectory,
    IReadOnlyList<string> FilesCreated,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>全部可用模板。</summary>
public static class ProjectTemplateCatalog
{
    public static IReadOnlyList<ProjectTemplate> All { get; } =
    [
        new ProjectTemplate(
            "multiboot2-c",
            "Multiboot 2 内核（C）",
            "GRUB 2 引导的内核骨架，串口输出一行字。适合第一次写内核。",
            "multiboot2",
            ["x86_64", "i686"]),

        new ProjectTemplate(
            "uefi-c",
            "UEFI 应用（C）",
            "被 UEFI 固件直接加载的 PE32+ 应用，调用固件控制台输出。",
            "uefi",
            ["x86_64", "aarch64"]),

        new ProjectTemplate(
            "bare-metal-c",
            "裸机内核（-kernel 直启）",
            "不经过引导程序，QEMU 直接把镜像加载到内存。迭代最快。",
            "direct_bare",
            []),

        new ProjectTemplate(
            "multiboot2-asm",
            "Multiboot 2 内核（纯汇编）",
            "不依赖 C 运行时，用于完整看清引导过程本身。",
            "multiboot2",
            ["x86_64"]),
    ];

    public static ProjectTemplate? Find(string id)
        => All.FirstOrDefault(template => string.Equals(template.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>某个架构 + 引导方式可用的模板。</summary>
    public static IReadOnlyList<ProjectTemplate> AvailableFor(string architectureId, string bootMethodId)
        => [.. All.Where(template =>
            string.Equals(template.BootMethodId, bootMethodId, StringComparison.OrdinalIgnoreCase)
            && (template.SupportedArchitectureIds.Count == 0
                || template.SupportedArchitectureIds.Contains(architectureId, StringComparer.OrdinalIgnoreCase)))];
}

/// <summary>
/// 把模板展开到磁盘。
///
/// 模板内容用**占位符 + 替换表**而不是字符串插值。
/// 模板里充满 C 的花括号、链接脚本的花括号、NASM 的 <c>%define</c>；
/// 用插值写会让每个花括号都要转义，读起来完全不像那个文件。
/// 占位符替换让模板正文保持"就是文件本身的样子"，
/// 这也是 dotnet new 与 cookiecutter 的做法。
/// </summary>
public static class ProjectScaffolder
{
    /// <summary>
    /// 生成项目。
    /// </summary>
    /// <param name="projectDirectory">目标目录。已存在且非空时会被拒绝，避免覆盖用户的东西。</param>
    /// <param name="template">模板。</param>
    /// <param name="architecture">目标架构。</param>
    /// <param name="projectName">项目名。</param>
    /// <param name="overwrite">目录非空时是否继续。</param>
    public static TemplateResult Create(
        string projectDirectory,
        ProjectTemplate template,
        ArchitectureDefinition architecture,
        string projectName,
        bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(architecture);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        var diagnostics = new List<Diagnostic>();
        var created = new List<string>();

        // 目录非空时拒绝：模板展开会写入多个文件，静默覆盖用户已有内容是不可接受的。
        if (Directory.Exists(projectDirectory)
            && Directory.EnumerateFileSystemEntries(projectDirectory).Any()
            && !overwrite)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFTEMPLATE001",
                $"目标目录不是空的：{projectDirectory}",
                Hint: "换一个目录，或勾选「允许写入非空目录」。"));
            return new TemplateResult(false, projectDirectory, created, diagnostics);
        }

        try
        {
            Directory.CreateDirectory(projectDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFTEMPLATE002",
                $"无法创建项目目录：{exception.Message}",
                projectDirectory,
                Exception: exception));
            return new TemplateResult(false, projectDirectory, created, diagnostics);
        }

        var tokens = BuildTokenTable(projectName, architecture);

        foreach (var (relativePath, rawContent) in TemplateContent.Expand(template.Id, architecture.Id))
        {
            var content = ApplyTokens(rawContent, tokens);
            var fullPath = Path.Combine(
                projectDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(fullPath, content, new System.Text.UTF8Encoding(false));
                created.Add(relativePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "MFTEMPLATE003",
                    $"写入 {relativePath} 失败：{exception.Message}",
                    fullPath,
                    Exception: exception));
            }
        }

        // 项目文件交给 ProjectFile：字段顺序与格式由它统一负责。
        try
        {
            ProjectFile.Save(projectDirectory, new MetalForgeProject
            {
                Name = projectName,
                Architecture = architecture.Id,
                BootMethod = template.BootMethodId,
                Configuration = "Debug",
                SourceDirectory = "src",
                LinkerScript = "linker.ld",
            });

            created.Add(ProjectFile.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFTEMPLATE004",
                $"写入 {ProjectFile.FileName} 失败：{exception.Message}",
                projectDirectory,
                Exception: exception));
        }

        return new TemplateResult(
            !diagnostics.Any(d => d.Severity >= DiagnosticSeverity.Error),
            projectDirectory,
            created,
            diagnostics);
    }

    /// <summary>构造占位符替换表。</summary>
    internal static Dictionary<string, string> BuildTokenTable(string projectName, ArchitectureDefinition architecture)
    {
        var is64Bit = architecture.Bitness == 64;

        // 栈建立、BSS 清零、停机循环：这三段是"没有引导程序时"必须自己做的事，
        // 且每种指令集写法完全不同。放在一张表里，读者能一眼看出差别。
        var (bootSource, bssClear, halt) = architecture.Id switch
        {
            "aarch64" => (
                "    /* 读当前异常级别；EL1 及以上才用我们自己的栈。 */\n"
                + "    mrs x0, CurrentEL\n    cmp x0, #0x8\n    b.eq 1f\n    ldr x0, =stack_top\n    mov sp, x0\n1:",
                "    ldr x0, =__bss_start\n    ldr x1, =__bss_end\n2:  cmp x0, x1\n    b.hs 3f\n    str xzr, [x0], #8\n    b 2b\n3:",
                "wfi\n    b 4b"),

            "riscv64" => (
                "    la sp, stack_top",
                "    la t0, __bss_start\n    la t1, __bss_end\n2:  bgeu t0, t1, 3f\n    sd zero, 0(t0)\n    addi t0, t0, 8\n    j 2b\n3:",
                "wfi\n    j 4b"),

            "riscv32" => (
                "    la sp, stack_top",
                "    la t0, __bss_start\n    la t1, __bss_end\n2:  bgeu t0, t1, 3f\n    sw zero, 0(t0)\n    addi t0, t0, 4\n    j 2b\n3:",
                "wfi\n    j 4b"),

            "arm32" or "cortex_m" => (
                "    ldr sp, =stack_top",
                "    ldr r0, =__bss_start\n    ldr r1, =__bss_end\n2:  cmp r0, r1\n    bhs 3f\n    mov r2, #0\n    str r2, [r0], #4\n    b 2b\n3:",
                "wfi\n    b 4b"),

            _ when is64Bit => (
                "    movq $stack_top, %rsp\n    /* 16 字节对齐：System V ABI 要求。 */\n    andq $-16, %rsp",
                "    leaq __bss_start(%rip), %rdi\n    leaq __bss_end(%rip), %rcx\n    subq %rdi, %rcx\n    xorl %eax, %eax\n    rep stosb",
                "cli\n    hlt\n    jmp 4b"),

            _ => (
                "    movl $stack_top, %esp\n    andl $-16, %esp",
                "    movl $__bss_start, %edi\n    movl $__bss_end, %ecx\n    subl %edi, %ecx\n    xorl %eax, %eax\n    rep stosb",
                "cli\n    hlt\n    jmp 4b"),
        };

        var loadAddress = architecture.Id switch
        {
            "cortex_m" => "0x00000000",
            "esp32" => "0x40000000",
            "aarch64" or "arm32" or "riscv64" or "riscv32" => "0x40000000",
            _ => "1M",
        };

        var debugInstruction = architecture.Id is "cortex_m" or "arm32" or "aarch64" or "riscv64" or "riscv32"
            ? "wfi"
            : "hlt";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["@@PROJECT_NAME@@"] = projectName,
            ["@@ARCH_DISPLAY@@"] = architecture.DisplayName,
            ["@@ARCH_ID@@"] = architecture.Id,
            ["@@ARCH_TRIPLE@@"] = architecture.TargetTriple,
            ["@@ARCH_NOTES@@"] = architecture.AbiNotes ?? string.Empty,
            ["@@BOOT_SOURCE@@"] = bootSource,
            ["@@BSS_CLEAR@@"] = bssClear,
            ["@@HALT@@"] = halt,
            ["@@DEBUG_INSTRUCTION@@"] = debugInstruction,
            ["@@LOAD_ADDRESS@@"] = loadAddress,
            ["@@QEMU_SYSTEM@@"] = architecture.QemuSystemExecutable,
            ["@@EFI_BOOT_NAME@@"] = architecture.Id == "aarch64" ? "BOOTAA64.EFI" : "BOOTX64.EFI",
        };
    }

    /// <summary>
    /// 应用占位符。
    /// 未替换的占位符**保留原样**：它们通常是模板自身的笔误，
    /// 留在生成的文件里看得见，比静默消失好。
    /// </summary>
    internal static string ApplyTokens(string content, IReadOnlyDictionary<string, string> tokens)
    {
        var result = content;

        foreach (var (token, value) in tokens)
        {
            result = result.Replace(token, value, StringComparison.Ordinal);
        }

        return result;
    }
}
