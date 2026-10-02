namespace MetalForge.Core.Diagnostics;

/// <summary>诊断严重级别。用于配置校验、构建输出、运行期问题的统一表达。</summary>
public enum DiagnosticSeverity
{
    /// <summary>提示信息，不影响功能。</summary>
    Info,

    /// <summary>可用但需要用户注意（例如回退到默认值）。</summary>
    Warning,

    /// <summary>该层配置或该次操作失败，已降级处理。</summary>
    Error,

    /// <summary>致命问题，相关功能不可用。</summary>
    Critical,
}

/// <summary>JSON Pointer 路径，例如 <c>/palette/accent</c>。空字符串表示文档根。</summary>
public readonly record struct JsonPointer(string Value)
{
    public static JsonPointer Root => new(string.Empty);

    public bool IsRoot => Value.Length == 0;

    /// <summary>追加一段路径。段内的 <c>~</c> 与 <c>/</c> 按 RFC 6901 转义。</summary>
    public JsonPointer Append(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var escaped = segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        return new JsonPointer(Value + "/" + escaped);
    }

    public JsonPointer AppendIndex(int index) => new(Value + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public override string ToString() => IsRoot ? "(root)" : Value;
}

/// <summary>
/// 一条结构化诊断。<see cref="Hint"/> 面向初学者给出"下一步怎么办"，
/// 这是本项目"人话报错"要求的承载点。
/// </summary>
/// <param name="Severity">严重级别。</param>
/// <param name="Code">稳定的机器可读代码，例如 <c>MFCFG001</c>；测试与文档依赖它。</param>
/// <param name="Message">面向用户的消息。</param>
/// <param name="Source">来源文件；无文件来源时为 null。</param>
/// <param name="Location">在来源文档中的位置（JSON Pointer）。命名刻意避开 "Pointer"，
/// 因为 CA1720 会把标识符里的类型名视为可读性问题。</param>
/// <param name="Line">1 起算的行号；未知为 null。</param>
/// <param name="Column">1 起算的列号；未知为 null。</param>
/// <param name="Hint">建议的修复动作说明。</param>
/// <param name="Exception">导致该诊断的异常（禁止吞异常：必须随诊断携带）。</param>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Source = null,
    JsonPointer? Location = null,
    int? Line = null,
    int? Column = null,
    string? Hint = null,
    Exception? Exception = null)
{
    public override string ToString()
    {
        var location = Source is null ? string.Empty : Source;
        if (Location is { IsRoot: false } pointer)
        {
            location += pointer.Value;
        }

        if (Line is not null)
        {
            location += $"({Line},{Column ?? 1})";
        }

        var suffix = Hint is null ? string.Empty : $" — 建议：{Hint}";
        return location.Length == 0
            ? $"[{Severity}] {Code}: {Message}{suffix}"
            : $"{location}: [{Severity}] {Code}: {Message}{suffix}";
    }
}
