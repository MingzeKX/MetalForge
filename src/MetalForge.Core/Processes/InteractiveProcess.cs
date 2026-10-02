using System.Diagnostics;
using System.Text;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Processes;

/// <summary>
/// 长驻交互式进程。用于 QEMU、GDB/MI、clangd(LSP)、OpenOCD 这类基于 stdio 的会话。
///
/// 与 <see cref="ProcessRunner.RunAsync"/> 的区别：不设超时、暴露标准输入、
/// 逐行回调输出，并在释放时确保整棵进程树被终止。
/// </summary>
internal sealed class InteractiveProcess : IInteractiveProcess
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly JobObjectHandle? _job;
    private readonly Action<Diagnostic>? _diagnosticSink;
    private readonly TaskCompletionSource<int> _exitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _standardErrorTail = new();
    private bool _disposed;

    public InteractiveProcess(ProcessRequest request, ProcessStartInfo startInfo, Action<Diagnostic>? diagnosticSink)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(startInfo);

        _diagnosticSink = diagnosticSink;
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        _process.OutputDataReceived += OnOutputDataReceived;
        _process.ErrorDataReceived += OnErrorDataReceived;
        _process.Exited += OnExited;

        try
        {
            if (!_process.Start())
            {
                throw new InvalidOperationException($"Process.Start 返回 false：{request.FileName}");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Report(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFPROC020",
                $"无法启动长驻进程 '{request.FileName}'：{exception.Message}",
                Hint: "确认工具已安装且路径正确；「工具 → 工具链健康检查」可查看探测结果。",
                Exception: exception));
            throw;
        }

        ProcessId = _process.Id;

        if (request.KillProcessTreeOnExit)
        {
            _job = JobObjectHandle.TryCreate(out _);
            if (_job is not null && !_job.TryAssignProcess(_process.Handle, out var failure) && failure is not null)
            {
                Report(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "MFPROC021",
                    $"无法把长驻进程加入 Job Object，残留清理将退化为遍历方式：{failure}"));
            }
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        StandardInput = _process.StandardInput;
        if (StandardInput is not null)
        {
            // 关键：显式设为不自动刷新，由 WriteLineAsync/WriteAsync 控制刷新时机。
            // LSP 需要精确的报文边界，自动刷新会把一条报文拆成多次写入。
            StandardInput.AutoFlush = false;
        }
    }

    public int ProcessId { get; } = -1;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public StreamWriter? StandardInput { get; }

    /// <summary>标准错误的尾部若干行，供协议解析失败时给出上下文。</summary>
    public string StandardErrorTail
    {
        get
        {
            lock (_standardErrorTail)
            {
                return _standardErrorTail.ToString();
            }
        }
    }

    public event EventHandler<ProcessOutputLine>? OutputReceived;

    public event EventHandler<int>? Exited;

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (StandardInput is null)
        {
            throw new InvalidOperationException("本次会话未重定向标准输入。");
        }

        await StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (StandardInput is null)
        {
            throw new InvalidOperationException("本次会话未重定向标准输入。");
        }

        await StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        await StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        if (HasExited)
        {
            return ExitCode ?? 0;
        }

        return await _exitSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Kill(bool entireTree = true)
    {
        try
        {
            if (!HasExited)
            {
                _process.Kill(entireTree);
            }
        }
        catch (InvalidOperationException)
        {
            // 已退出，竞态正常。
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Report(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFPROC022",
                $"终止长驻进程失败：{exception.Message}",
                Exception: exception));
        }

        _job?.TryTerminateAll(out _);
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is null)
        {
            return;
        }

        OutputReceived?.Invoke(this, new ProcessOutputLine(DateTimeOffset.Now, IsError: false, args.Data));
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is null)
        {
            return;
        }

        // 保留尾部若干行：协议会话出错时，原因往往只出现在 stderr。
        lock (_standardErrorTail)
        {
            _standardErrorTail.AppendLine(args.Data);
            if (_standardErrorTail.Length > 16 * 1024)
            {
                _standardErrorTail.Remove(0, _standardErrorTail.Length - 8 * 1024);
            }
        }

        OutputReceived?.Invoke(this, new ProcessOutputLine(DateTimeOffset.Now, IsError: true, args.Data));
    }

    private void OnExited(object? sender, EventArgs args)
    {
        var exitCode = ExitCode ?? -1;
        _exitSource.TrySetResult(exitCode);
        Exited?.Invoke(this, exitCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Kill();

        // 给内核一点时间回收，避免句柄泄漏与僵尸进程。
        try
        {
            await _exitSource.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Report(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFPROC023",
                $"长驻进程 {ProcessId} 在 5 秒内未退出，已强制清理 Job Object。",
                Hint: "若反复出现，请上报；这通常意味着某个外部工具的退出行为异常。"));
        }
        catch (OperationCanceledException)
        {
            // 不会发生：等待用的是超时而非取消令牌。
        }

        _process.OutputDataReceived -= OnOutputDataReceived;
        _process.ErrorDataReceived -= OnErrorDataReceived;
        _process.Exited -= OnExited;

        try
        {
            StandardInput?.Dispose();
        }
        catch (IOException)
        {
            // 子进程已关闭管道。
        }

        _process.Dispose();
        _job?.Dispose();
    }

    private void Report(Diagnostic diagnostic)
    {
        if (_diagnosticSink is not null)
        {
            _diagnosticSink(diagnostic);
            return;
        }

        Trace.WriteLine(diagnostic.ToString());
    }
}
