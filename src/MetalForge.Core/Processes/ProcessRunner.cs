using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Processes;

/// <summary>
/// <see cref="IProcessRunner"/> 的默认实现。
///
/// 实现纪律（逐条对应 DESIGN.md §12，都是会真实咬人的东西）：
///   1. 必须异步订阅读取 stdout/stderr —— 否则管道缓冲区填满后子进程阻塞，双方互等死锁；
///   2. 一律使用 ArgumentList，绝不拼接命令行字符串；
///   3. 标准流显式设为 UTF-8，避免中文/符号乱码；
///   4. 超时与取消走同一条终止路径，并标记 TimedOut/Canceled（Windows 上被强杀的进程退出码是 1，
///      只看退出码会把"超时"误判成"构建失败"）；
///   5. Windows 上用 Job Object 兜底终止整棵进程树；
///   6. 任何异常都转成 <see cref="Diagnostic"/> 或写入返回结果，绝不静默吞掉。
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>单个流的行缓冲上限，防止无换行的海量输出把内存吃光。</summary>
    private const int MaxBufferedLineLength = 64 * 1024;

    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Action<Diagnostic>? _diagnosticSink;

    /// <param name="diagnosticSink">
    /// 内部异常的去处。为 null 时写入 <see cref="Trace"/>：
    /// 允许为 null 不代表允许丢弃（禁止异常吞没），只是把去向交给调用方决定。
    /// </param>
    public ProcessRunner(Action<Diagnostic>? diagnosticSink = null)
    {
        _diagnosticSink = diagnosticSink;
    }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<ProcessOutputLine>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var lines = new ConcurrentQueue<ProcessOutputLine>();
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = BuildStartInfo(request) };
        using var job = request.KillProcessTreeOnExit ? JobObjectHandle.TryCreate(out _) : null;

        // 标准输出与标准错误必须用两个独立的事件处理器。
        // 不能用 "ReferenceEquals(sender, process)" 区分来源：在两种事件里
        // sender 都是那个 Process 对象，它无法表达"这一行来自哪个流"。
        // 本项目第一版正是这样写的，导致所有 stdout 被标记成 stderr。
        void OnStandardOutput(object sender, DataReceivedEventArgs args) => OnOutput(args, isError: false);

        void OnStandardError(object sender, DataReceivedEventArgs args) => OnOutput(args, isError: true);

        void OnOutput(DataReceivedEventArgs args, bool isError)
        {
            if (args.Data is null)
            {
                return;
            }

            var line = new ProcessOutputLine(DateTimeOffset.Now, isError, args.Data);
            lines.Enqueue(line);

            // 全文与逐行回调各自独立累积：即使调用方不订阅，也要留下完整输出供解析。
            lock (standardOutput)
            {
                if (isError)
                {
                    standardError.AppendLine(args.Data);
                }
                else
                {
                    standardOutput.AppendLine(args.Data);
                }
            }

            progress?.Report(line);
        }

        process.OutputDataReceived += OnStandardOutput;
        process.ErrorDataReceived += OnStandardError;

        var timedOut = false;
        var canceled = false;

        try
        {
            if (!process.Start())
            {
                return FailedToStart(request, stopwatch.Elapsed, standardOutput.ToString(), standardError.ToString(), lines, new InvalidOperationException("Process.Start 返回 false。"));
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // 工具不存在、权限不足、路径非法都会走到这里。
            // 这不是"编码错误"，而是"环境未就绪"，调用方据此触发工具链引导（G-01）。
            return FailedToStart(request, stopwatch.Elapsed, string.Empty, string.Empty, lines, exception);
        }

        if (job is not null && !job.TryAssignProcess(process.Handle, out var assignFailure) && assignFailure is not null)
        {
            // 加入 Job 失败不致命：退化为按进程树终止，但要留下记录。
            Report(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFPROC001",
                $"无法把进程加入 Job Object，进程树终止将退化为遍历方式：{assignFailure}"));
        }

        // 必须在 Start 之后调用，且两个流都要开始读取。
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (request.StandardInput is { } input)
        {
            try
            {
                await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
                // 有意不传播 cancellationToken：写入的是已经确定要发送的内容，
                // 取消语义由随后的 WaitForExit 承担。传 CancellationToken.None 以表明是刻意的。
                await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException exception)
            {
                // 子进程可能在读入前就退出（例如参数错误）。这不是失败，交由退出码体现。
                Report(new Diagnostic(
                    DiagnosticSeverity.Info,
                    "MFPROC002",
                    $"写入标准输入时通道已关闭（子进程可能已退出）：{exception.Message}",
                    Exception: exception));
            }
        }

        using var timeoutSource = request.Timeout is { } timeout
            ? new CancellationTokenSource(timeout)
            : new CancellationTokenSource();

        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = cancellationToken.IsCancellationRequested;
            timedOut = !canceled && timeoutSource.IsCancellationRequested;
            TerminateProcess(process, job, request);
        }

        // 结束异步读取并等待缓冲区排空。
        // 没有这一步，WaitForExit 返回后仍可能有尚未回调的输出行（.NET 的已知行为）。
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or SystemException)
        {
            Report(new Diagnostic(
                DiagnosticSeverity.Warning,
                "MFPROC003",
                $"等待输出排空时失败：{exception.Message}",
                Exception: exception));
        }

        process.OutputDataReceived -= OnStandardOutput;
        process.ErrorDataReceived -= OnStandardError;
        stopwatch.Stop();

        return new ProcessResult
        {
            ExitCode = SafeExitCode(process),
            TimedOut = timedOut,
            Canceled = canceled,
            FailedToStart = false,
            Duration = stopwatch.Elapsed,
            Lines = [.. lines],
            StandardOutput = standardOutput.ToString(),
            StandardError = standardError.ToString(),
        };
    }

    public IInteractiveProcess StartInteractive(ProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startInfo = BuildStartInfo(request);
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        return new InteractiveProcess(request, startInfo, _diagnosticSink);
    }

    private static ProcessStartInfo BuildStartInfo(ProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = _utf8NoBom,
            StandardErrorEncoding = _utf8NoBom,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };

        // 结构化参数：运行时负责转义，含空格的路径不会被拆开。
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in request.Environment)
        {
            if (pair.Value is null)
            {
                startInfo.Environment.Remove(pair.Key);
            }
            else
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        if (request.StandardInput is not null)
        {
            startInfo.RedirectStandardInput = true;
            startInfo.StandardInputEncoding = _utf8NoBom;
        }

        // 让子进程的输出尽量不被自身缓冲（很多工具据此判断是否为终端）。
        if (!startInfo.Environment.ContainsKey("NO_COLOR"))
        {
            startInfo.Environment["NO_COLOR"] = "1";
        }

        return startInfo;
    }

    private static void TerminateProcess(Process process, JobObjectHandle? job, ProcessRequest request)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: request.KillProcessTreeOnExit);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已经退出，竞态正常。
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 终止失败时继续走 Job Object 兜底。
        }

        // Job Object 是内核级兜底：即使上面的遍历漏掉子孙进程，关闭 Job 句柄也会全部清理。
        job?.TryTerminateAll(out _);
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private ProcessResult FailedToStart(
        ProcessRequest request,
        TimeSpan duration,
        string standardOutput,
        string standardError,
        ConcurrentQueue<ProcessOutputLine> lines,
        Exception exception)
    {
        Report(new Diagnostic(
            DiagnosticSeverity.Error,
            "MFPROC010",
            $"无法启动进程 '{request.FileName}'：{exception.Message}",
            Hint: "通常是工具未安装或路径不正确。打开「工具 → 工具链健康检查」查看缺失项与获取方式。",
            Exception: exception));

        return new ProcessResult
        {
            ExitCode = null,
            FailedToStart = true,
            StartupException = exception,
            Duration = duration,
            Lines = [.. lines],
            StandardOutput = standardOutput,
            StandardError = standardError,
        };
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
