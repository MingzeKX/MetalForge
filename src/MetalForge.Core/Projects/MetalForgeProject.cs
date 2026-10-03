using System.Text.Json;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Projects;

/// <summary>
/// 一个 MetalForge 项目的配置，对应仓库里的 <c>metalforge.json</c>。
///
/// 放在项目根而不是藏在 IDE 数据库里：项目要求"所有生成物都是仓库内可见的文本文件"，
/// 这样构建配置能被评审、能被 diff、能脱离 IDE 复现。
/// </summary>
public sealed record MetalForgeProject
{
    /// <summary>配置格式版本，用于将来迁移。</summary>
    public int SchemaVersion { get; init; } = 1;

    public string Name { get; init; } = "untitled";

    /// <summary>目标架构 id，对应 <c>assets/targets/architectures.json</c>。</summary>
    public string Architecture { get; init; } = "x86_64";

    /// <summary>引导方式 id，对应 <c>assets/targets/boot-methods.json</c>。</summary>
    public string BootMethod { get; init; } = "multiboot2";

    /// <summary>构建配置名（Debug/Release）。</summary>
    public string Configuration { get; init; } = "Debug";

    /// <summary>源文件根目录，相对项目根。</summary>
    public string SourceDirectory { get; init; } = "src";

    /// <summary>链接脚本路径，相对项目根；null 表示由工具链默认提供。</summary>
    public string? LinkerScript { get; init; }

    /// <summary>构建系统类型。</summary>
    public string BuildSystem { get; init; } = "make";

    /// <summary>项目自定义的编译/链接参数（会追加到工具链默认参数之后）。</summary>
    public IReadOnlyList<string> CompilerFlags { get; init; } = [];

    public IReadOnlyList<string> LinkerFlags { get; init; } = [];

    /// <summary>工具路径覆盖：工具 id → 目录。用于项目指定特定版本的工具链。</summary>
    public IReadOnlyDictionary<string, string> ToolchainOverrides { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>脚本钩子：事件名 → 命令行。见 DESIGN.md G-11。</summary>
    public IReadOnlyDictionary<string, string> Hooks { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>运行配置：QEMU 参数模板名等。</summary>
    public RunConfiguration Run { get; init; } = new();

    /// <summary>
    /// 结构性校验。与布局预设同理：配置里自相矛盾的地方在这里被发现，
    /// 而不是等到构建时抛异常。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("name 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(Architecture))
        {
            problems.Add("architecture 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(BootMethod))
        {
            problems.Add("bootMethod 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(SourceDirectory))
        {
            problems.Add("sourceDirectory 不能为空。");
        }

        foreach (var hook in Hooks)
        {
            if (string.IsNullOrWhiteSpace(hook.Value))
            {
                problems.Add($"钩子 '{hook.Key}' 的命令为空。");
            }
        }

        return problems;
    }
}

/// <summary>运行（QEMU）相关配置。</summary>
public sealed record RunConfiguration
{
    /// <summary>QEMU 机器类型覆盖（默认由架构矩阵决定）。</summary>
    public string? Machine { get; init; }

    /// <summary>内存大小（MB）。</summary>
    public int MemoryMegabytes { get; init; } = 256;

    /// <summary>是否把串口接到标准输出。内核日志的主要通道。</summary>
    public bool SerialToStdout { get; init; } = true;

    /// <summary>是否启用 GDB stub（<c>-s -S</c> 的等价形式）。</summary>
    public bool GdbStub { get; init; }

    /// <summary>GDB stub 端口。</summary>
    public int GdbPort { get; init; } = 1234;

    /// <summary>额外的 QEMU 参数。</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>固件覆盖（OVMF 等）。null 表示自动探测。</summary>
    public string? FirmwarePath { get; init; }
}

/// <summary>加载结果：项目 + 诊断。</summary>
/// <param name="Project">解析出的项目；失败时为 null。</param>
/// <param name="Path">实际读取的文件路径。</param>
/// <param name="Diagnostics">解析过程中的全部诊断。</param>
public sealed record ProjectLoadResult(MetalForgeProject? Project, string? Path, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Succeeded => Project is not null && !Diagnostics.Any(d => d.Severity >= DiagnosticSeverity.Error);
}

/// <summary>
/// 读写 <c>metalforge.json</c>。
///
/// 默认使用仓库根直下的 <c>metalforge.json</c>（而不是藏在 <c>.metalforge/</c> 里）：
/// 这是项目的主配置文件，应当一眼可见、便于评审。
/// <c>.metalforge/</c> 仍然用于"本地覆盖"（不进版本控制）。
/// </summary>
public static class ProjectFile
{
    /// <summary>项目主配置文件名。</summary>
    public const string FileName = "metalforge.json";

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        // 中文项目名不应被转义成 \uXXXX
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>项目根目录下是否存在项目文件。</summary>
    public static bool Exists(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        return File.Exists(Path.Combine(projectDirectory, FileName));
    }

    /// <summary>
    /// 从目录加载项目。
    /// 文件不存在或内容损坏时返回失败结果与可读诊断，不抛异常 ——
    /// "打不开项目"应该表现为一条说明，而不是崩溃。
    /// </summary>
    public static ProjectLoadResult Load(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        var path = Path.Combine(projectDirectory, FileName);
        var diagnostics = new List<Diagnostic>();

        if (!File.Exists(path))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFPROJ001",
                $"未找到项目文件：{FileName}",
                path,
                Hint: "确认打开的是项目根目录，而不是它的子目录。"));
            return new ProjectLoadResult(null, path, diagnostics);
        }

        string text;
        try
        {
            text = File.ReadAllText(path, System.Text.Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFPROJ002",
                $"无法读取项目文件：{exception.Message}",
                path,
                Hint: "检查文件权限或是否被其他程序占用。",
                Exception: exception));
            return new ProjectLoadResult(null, path, diagnostics);
        }

        MetalForgeProject? project;
        try
        {
            project = JsonSerializer.Deserialize<MetalForgeProject>(text, _readOptions);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFPROJ003",
                $"项目文件语法错误（第 {exception.LineNumber + 1} 行）：{exception.Message}",
                path,
                Line: (int?)(exception.LineNumber + 1),
                Column: (int?)(exception.BytePositionInLine + 1),
                Hint: "常见原因：少了逗号、引号不配对。",
                Exception: exception));
            return new ProjectLoadResult(null, path, diagnostics);
        }

        if (project is null)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFPROJ004",
                "项目文件内容为空。",
                path,
                Hint: "空文件不构成项目；请填入 name / architecture / bootMethod 等字段。"));
            return new ProjectLoadResult(null, path, diagnostics);
        }

        foreach (var problem in project.Validate())
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFPROJ005",
                $"项目配置不合法：{problem}",
                path,
                Hint: "对照 docs/adr 与 DESIGN.md 里的字段说明修正。"));
        }

        return new ProjectLoadResult(project, path, diagnostics);
    }

    /// <summary>保存项目配置。写入使用 UTF-8 无 BOM，便于跨平台 diff。</summary>
    public static void Save(string projectDirectory, MetalForgeProject project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(project);

        Directory.CreateDirectory(projectDirectory);
        var path = Path.Combine(projectDirectory, FileName);
        var json = JsonSerializer.Serialize(project, _writeOptions);
        File.WriteAllText(path, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
