namespace MetalForge.Core.Processes;

/// <summary>
/// 一次外部进程调用的请求描述。
///
/// 关键设计：参数是 <see cref="IReadOnlyList{T}"/> 而不是拼接好的字符串。
/// 路径里的空格、引号、反斜杠由运行时负责转义；手工拼字符串是这类系统最常见的错误来源。
/// </summary>
public sealed record ProcessRequest
{
    /// <summary>可执行文件的绝对路径或位于 PATH 中的名称。</summary>
    public required string FileName { get; init; }

    /// <summary>参数列表（不含可执行文件本身）。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>工作目录；null 表示继承当前进程。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>附加或覆盖的环境变量；值为 null 表示删除该变量。</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>超时；null 表示不设上限（长驻进程应使用 <see cref="IProcessRunner.StartInteractive"/>）。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>写入标准输入的内容；null 表示不重定向标准输入。</summary>
    public string? StandardInput { get; init; }

    /// <summary>是否在进程结束时终止整棵进程树（默认 true，避免 QEMU 等派生进程残留）。</summary>
    public bool KillProcessTreeOnExit { get; init; } = true;

    /// <summary>给用户的说明，用于日志与"将要执行什么"的展示。</summary>
    public string? Description { get; init; }
}

/// <summary>进程输出的一行（或一段，视子进程刷新方式而定）。</summary>
/// <param name="Timestamp">到达时间。</param>
/// <param name="IsError">是否来自标准错误。</param>
/// <param name="Text">文本内容（已按 UTF-8 解码）。</param>
public readonly record struct ProcessOutputLine(DateTimeOffset Timestamp, bool IsError, string Text);

/// <summary>一次进程调用的最终结果。</summary>
public sealed record ProcessResult
{
    /// <summary>退出码；进程未能启动时为 null。</summary>
    public int? ExitCode { get; init; }

    public bool TimedOut { get; init; }

    public bool Canceled { get; init; }

    /// <summary>是否因为可执行文件不存在或无法启动而失败。</summary>
    public bool FailedToStart { get; init; }

    /// <summary>启动失败的原因（<see cref="FailedToStart"/> 为 true 时有值）。</summary>
    public Exception? StartupException { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>按到达顺序合并后的输出行。</summary>
    public required IReadOnlyList<ProcessOutputLine> Lines { get; init; }

    /// <summary>标准输出全文（含换行）。</summary>
    public required string StandardOutput { get; init; }

    /// <summary>标准错误全文（含换行）。</summary>
    public required string StandardError { get; init; }

    /// <summary>
    /// 是否成功。
    /// 注意：在 Windows 上被强制终止的进程退出码为 1，因此不能只看退出码判断超时/取消。
    /// </summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Canceled && !FailedToStart;

    /// <summary>把输出合并为文本，便于解析（例如解析工具版本号）。</summary>
    public string CombinedOutput => StandardOutput + StandardError;

    /// <summary>诊断用的一行摘要。</summary>
    public string Describe()
    {
        if (FailedToStart)
        {
            return $"启动失败：{StartupException?.Message ?? "未知原因"}";
        }

        if (TimedOut)
        {
            return $"超时（{Duration.TotalSeconds:F1}s）后终止";
        }

        if (Canceled)
        {
            return "已取消";
        }

        return $"退出码 {ExitCode}（{Duration.TotalSeconds:F1}s）";
    }
}

/// <summary>批量输出回调。用 <see cref="IProgress{T}"/> 而不是事件，便于把回调切回 UI 线程。</summary>
public interface IProcessOutputSink
{
    void Report(ProcessOutputLine line);
}

/// <summary>运行一次性进程。</summary>
public interface IProcessRunner
{
    /// <summary>
    /// 运行进程并等待结束。<paramref name="progress"/> 为 null 时不逐行回调（仍会收集全文）。
    /// 本方法不抛出"进程失败"类异常：失败信息在返回值里，便于调用方统一转成诊断。
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutputLine>? progress, CancellationToken cancellationToken);

    /// <summary>启动长驻交互式进程（QEMU / GDB / clangd / OpenOCD）。</summary>
    IInteractiveProcess StartInteractive(ProcessRequest request);
}
