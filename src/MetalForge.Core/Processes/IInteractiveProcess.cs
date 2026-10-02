namespace MetalForge.Core.Processes;

/// <summary>
/// 长驻交互式进程的句柄。
///
/// 生命周期契约：调用方必须在结束会话时 <c>await using</c> 释放，
/// 或显式调用 <see cref="Kill"/>。窗口关闭、项目切换都必须走到这里，
/// 否则 QEMU/clangd 这类进程会在用户机器上长期残留。
/// </summary>
public interface IInteractiveProcess : IAsyncDisposable
{
    /// <summary>进程 id；未成功启动时为 -1。</summary>
    int ProcessId { get; }

    bool HasExited { get; }

    /// <summary>进程退出码；尚未退出时为 null。</summary>
    int? ExitCode { get; }

    /// <summary>标准输出/错误到达。</summary>
    event EventHandler<ProcessOutputLine>? OutputReceived;

    /// <summary>进程退出。</summary>
    event EventHandler<int>? Exited;

    /// <summary>标准输入写入器（用于 GDB MI、clangd LSP 等基于 stdio 的协议）。</summary>
    StreamWriter? StandardInput { get; }

    /// <summary>写入一行并刷新。</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>写入原始文本并刷新（LSP 需要精确控制报文边界）。</summary>
    Task WriteAsync(string text, CancellationToken cancellationToken);

    /// <summary>等待退出。</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>终止进程；<paramref name="entireTree"/> 为 true 时连同子进程一起终止。</summary>
    void Kill(bool entireTree = true);
}
