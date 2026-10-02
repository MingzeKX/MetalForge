namespace MetalForge.Core.Layout;

/// <summary>标签页类型。决定它被放在文档区（可编辑）还是停靠面板（工具视图）。</summary>
public enum TabKind
{
    /// <summary>文档标签页：可编辑、可关闭、可多开（例如编辑器、欢迎页）。</summary>
    Document,

    /// <summary>工具标签页：位于停靠面板内，全局唯一。</summary>
    Tool,
}

/// <summary>
/// 一个界面标签页的声明。
///
/// 为什么这个目录放在 Core 而不是 App：
/// 布局 JSON 里的标签页 id 必须指向真实存在的实现。若把 id 和实现都散落在
/// XAML 与 C# 里，那么"布局引用了一个尚未实现的标签页"只会在运行时表现为空白面板。
/// 集中声明之后，这件事可以被单元测试拦住（见 TabCatalogTests）。
/// </summary>
/// <param name="Id">稳定标识，也是本地化键 <c>tab.&lt;camelCase&gt;</c> 的来源。</param>
/// <param name="Kind">文档或工具。</param>
/// <param name="ImplementationKey">界面实现键；App 层据此选择视图。避免把类型名写进配置。</param>
/// <param name="LocalizationKey">标题的文案键。</param>
/// <param name="IconKey">图标键（可选，当前为文本/几何标识）。</param>
/// <param name="Closable">是否允许用户关闭。</param>
public sealed record TabDescriptor(
    string Id,
    TabKind Kind,
    string ImplementationKey,
    string LocalizationKey,
    string? IconKey = null,
    bool Closable = true)
{
    /// <summary>该标签页是否在给定布局中被引用。</summary>
    public bool IsReferencedBy(LayoutPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return preset.AllTabIds().Contains(Id, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 内置标签页目录。新增标签页时在这里登记，并同时在语言文件里补 <c>tab.&lt;key&gt;</c>。
/// </summary>
public static class TabCatalog
{
    /// <summary>全部内置标签页（按声明顺序，用作界面展示顺序）。</summary>
    public static IReadOnlyList<TabDescriptor> All { get; } =
    [
        // ---- 文档区 ----
        new("welcome", TabKind.Document, "welcome", "tab.welcome", IconKey: "sparkle", Closable: true),
        new("editor", TabKind.Document, "codeEditor", "tab.editor", IconKey: "file", Closable: true),
        new("about", TabKind.Document, "about", "tab.about", Closable: true),
        new("settings", TabKind.Document, "settings", "tab.settings", Closable: true),

        // ---- 左侧面板 ----
        new("project.files", TabKind.Tool, "projectFiles", "tab.projectFiles", IconKey: "folder"),
        new("project.templates", TabKind.Tool, "projectTemplates", "tab.projectTemplates", IconKey: "template"),
        new("project.symbols", TabKind.Tool, "projectSymbols", "tab.projectSymbols", IconKey: "symbol"),

        // ---- 右侧面板 ----
        new("inspector.properties", TabKind.Tool, "inspectorProperties", "tab.inspectorProperties", IconKey: "sliders"),
        new("inspector.artifact", TabKind.Tool, "inspectorArtifact", "tab.inspectorArtifact", IconKey: "box"),
        new("inspector.layoutMap", TabKind.Tool, "inspectorLayoutMap", "tab.inspectorLayoutMap", IconKey: "map"),

        // ---- 底部面板 ----
        new("output.build", TabKind.Tool, "outputBuild", "tab.outputBuild", IconKey: "terminal"),
        new("output.problems", TabKind.Tool, "outputProblems", "tab.outputProblems", IconKey: "warning"),
        new("terminal.shell", TabKind.Tool, "terminalShell", "tab.terminalShell", IconKey: "terminal"),
        new("terminal.serial", TabKind.Tool, "terminalSerial", "tab.terminalSerial", IconKey: "plug"),
        new("debug.console", TabKind.Tool, "debugConsole", "tab.debugConsole", IconKey: "bug"),
        new("debug.monitor", TabKind.Tool, "debugMonitor", "tab.debugMonitor", IconKey: "monitor"),
        new("ai.agent", TabKind.Tool, "aiAgent", "tab.aiAgent", IconKey: "sparkle"),

        // ---- 调试布局专用（右侧） ----
        new("debug.stack", TabKind.Tool, "debugStack", "tab.debugStack", IconKey: "layers"),
        new("debug.variables", TabKind.Tool, "debugVariables", "tab.debugVariables", IconKey: "variable"),
        new("debug.registers", TabKind.Tool, "debugRegisters", "tab.debugRegisters", IconKey: "chip"),
        new("debug.breakpoints", TabKind.Tool, "debugBreakpoints", "tab.debugBreakpoints", IconKey: "dot"),
        new("debug.memory", TabKind.Tool, "debugMemory", "tab.debugMemory", IconKey: "grid"),

        // ---- 工具链 ----
        new("toolchain.health", TabKind.Tool, "toolchainHealth", "tab.toolchainHealth", IconKey: "wrench"),
    ];

    private static readonly Dictionary<string, TabDescriptor> _byId =
        All.ToDictionary(tab => tab.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>按 id 查找。</summary>
    public static TabDescriptor? Find(string tabId)
        => tabId is not null && _byId.TryGetValue(tabId, out var descriptor) ? descriptor : null;

    /// <summary>全部 id（用于校验函数）。</summary>
    public static IReadOnlySet<string> Ids { get; } = new HashSet<string>(All.Select(tab => tab.Id), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 校验一个布局引用的标签页是否都已登记。
    /// 未登记的标签页会让界面出现无法解释的空白，因此必须在测试里被拦住。
    /// </summary>
    public static IReadOnlyList<string> FindUnregisteredTabs(LayoutPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return [.. preset.AllTabIds().Where(tabId => !Ids.Contains(tabId))];
    }
}
