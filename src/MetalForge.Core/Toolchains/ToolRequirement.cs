namespace MetalForge.Core.Toolchains;

/// <summary>工具在文件系统中的来源层级。顺序即探测优先级。</summary>
public enum ToolSource
{
    /// <summary>未找到。</summary>
    NotFound,

    /// <summary>项目配置显式指定（<c>metalforge.json</c>）。</summary>
    ProjectConfiguration,

    /// <summary>用户配置显式指定。</summary>
    UserConfiguration,

    /// <summary>MetalForge 托管目录（应用内下载安装的工具）。</summary>
    ManagedDirectory,

    /// <summary>系统 PATH。</summary>
    SystemPath,

    /// <summary>常见安装位置白名单。</summary>
    KnownLocation,
}

/// <summary>工具的可用性状态。</summary>
public enum ToolStatus
{
    /// <summary>找不到可执行文件。</summary>
    Missing,

    /// <summary>找到了，但版本低于要求。</summary>
    Outdated,

    /// <summary>找到了且版本满足要求。</summary>
    Ready,

    /// <summary>找到了但无法执行（权限、损坏、依赖缺失）。</summary>
    Broken,
}

/// <summary>
/// 一个被探测到的工具实例。
/// </summary>
/// <param name="ToolId">工具标识，例如 <c>cmake</c>。</param>
/// <param name="ExecutablePath">可执行文件绝对路径。</param>
/// <param name="Version">探测到的版本；无法解析时为 null。</param>
/// <param name="Source">来源层级。</param>
/// <param name="RawVersionOutput">用于解析版本的原始输出（诊断用）。</param>
public sealed record ToolInstance(
    string ToolId,
    string ExecutablePath,
    Version? Version,
    ToolSource Source,
    string? RawVersionOutput = null)
{
    /// <summary>可执行文件所在目录。</summary>
    public string Directory => Path.GetDirectoryName(ExecutablePath) ?? string.Empty;
}

/// <summary>
/// 一个工具的期望值（来自 <c>assets/targets/tools.json</c>）。
/// </summary>
public sealed record ToolRequirement
{
    /// <summary>工具标识。</summary>
    public required string ToolId { get; init; }

    /// <summary>面向用户的名称。</summary>
    public required string DisplayName { get; init; }

    /// <summary>它在 OSDev 流程里干什么（缺失时要让用户明白影响）。</summary>
    public required string Purpose { get; init; }

    /// <summary>可执行文件名（含扩展名）。</summary>
    public required string ExecutableName { get; init; }

    /// <summary>用于获取版本号的参数，例如 <c>--version</c>。</summary>
    public IReadOnlyList<string> VersionArguments { get; init; } = ["--version"];

    /// <summary>从版本输出中提取版本号的正则（第一个捕获组即版本）。</summary>
    public string? VersionPattern { get; init; }

    /// <summary>最低可接受版本（含）。</summary>
    public string? MinimumVersion { get; init; }

    /// <summary>常见安装目录（支持 <c>%ProgramFiles%</c> 等环境变量展开）。</summary>
    public IReadOnlyList<string> KnownLocations { get; init; } = [];

    /// <summary>获取方式说明（面向用户）。</summary>
    public string? AcquisitionHint { get; init; }

    /// <summary>官方下载页。</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>属于哪一类（工具链/模拟器/构建/嵌入式），用于界面分组。</summary>
    public string Category { get; init; } = "general";

    /// <summary>是否必需。非必需工具缺失不降低整体状态。</summary>
    public bool Required { get; init; } = true;

    /// <summary>解析后的最低版本；未指定时为 null。</summary>
    public Version? ParsedMinimumVersion => Version.TryParse(MinimumVersion, out var version) ? version : null;
}

/// <summary>单个工具的探测结果。</summary>
public sealed record ToolHealth
{
    public required ToolRequirement Requirement { get; init; }

    /// <summary>最终采用的实例；未找到时为 null（即使找到了多个候选也只采用优先级最高的）。</summary>
    public ToolInstance? Instance { get; init; }

    /// <summary>全部候选实例（用于界面展示"还有哪些可切换"）。</summary>
    public IReadOnlyList<ToolInstance> Candidates { get; init; } = [];

    public required ToolStatus Status { get; init; }

    /// <summary>状态说明（面向用户，缺失时要可执行）。</summary>
    public string? StatusDetail { get; init; }

    /// <summary>若该工具由本次探测实际执行过版本命令，这里记录命令耗时，便于识别卡住的工具。</summary>
    public TimeSpan? ProbeDuration { get; init; }

    public bool IsUsable => Status == ToolStatus.Ready;
}

/// <summary>整体工具链就绪程度。</summary>
public enum ToolchainReadiness
{
    /// <summary>全部必需工具就绪。</summary>
    Ready,

    /// <summary>部分必需工具缺失或版本过低。</summary>
    Degraded,

    /// <summary>关键工具全部缺失，无法进行任何构建。</summary>
    Unusable,
}

/// <summary>一次完整探测的报告。</summary>
public sealed record ToolHealthReport
{
    public required IReadOnlyList<ToolHealth> Tools { get; init; }
    public required ToolchainReadiness Readiness { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>按类别分组的工具（保持 JSON 中的声明顺序）。</summary>
    public IEnumerable<IGrouping<string, ToolHealth>> ByCategory()
        => Tools.GroupBy(tool => tool.Requirement.Category);

    /// <summary>缺失或不可用的必需工具。</summary>
    public IReadOnlyList<ToolHealth> MissingRequiredTools()
        => [.. Tools.Where(tool => tool.Requirement.Required && !tool.IsUsable)];

    /// <summary>可以直接展示给用户的一句话结论。</summary>
    public string Describe()
    {
        var required = Tools.Count(tool => tool.Requirement.Required);
        var ready = Tools.Count(tool => tool.Requirement.Required && tool.IsUsable);

        return Readiness switch
        {
            ToolchainReadiness.Ready => $"全部 {required} 项必需工具就绪。",
            ToolchainReadiness.Degraded => $"必需工具 {ready}/{required} 就绪，缺少 {required - ready} 项。",
            _ => $"未检测到可用工具链（0/{required}）。",
        };
    }
}
