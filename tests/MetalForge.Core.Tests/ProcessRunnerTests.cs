using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MetalForge.Core.Diagnostics;
using MetalForge.Core.Processes;

namespace MetalForge.Core.Tests;

/// <summary>
/// 进程执行器的真实调用测试。
///
/// 刻意不使用 mock：这一层要防的正是"只在真实进程下才出现"的问题
/// （管道填满死锁、编码、超时后进程残留、取消不彻底）。
///
/// 关于取消令牌：本项目刻意不使用 xunit 的 <c>TestContext.Current.CancellationToken</c>。
/// 该令牌表达的是"整个测试运行被中止"，而这里的取消测试需要一个**由测试自己控制**的
/// 令牌来验证取消路径，两者的语义不同。使用自定义令牌是刻意的，因此抑制 xUnit1051。
/// 每个测试仍带有硬上限，CI 不会被挂住。
/// </summary>
#pragma warning disable xUnit1051 // 取消令牌语义由测试自己控制，见上

public sealed class ProcessRunnerTests
{
    /// <summary>所有进程测试的硬上限：超过即视为失败，而不是无限等待。</summary>
    private static readonly TimeSpan TestSafetyTimeout = TimeSpan.FromSeconds(30);

    private static string ShellPath => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
        : "/bin/sh";

    private static ProcessRequest ShellRequest(string command, TimeSpan? timeout = null, string? workingDirectory = null)
        => new()
        {
            FileName = ShellPath,
            Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ["/c", command] : ["-c", command],
            Timeout = timeout,
            WorkingDirectory = workingDirectory,
        };

    private static string LongRunningCommand => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "ping -n 30 127.0.0.1 > nul"
        : "sleep 30";

    [Fact]
    public async Task RunsProcess_AndCapturesOutput()
    {
        var runner = new ProcessRunner();
        using var safety = new CancellationTokenSource(TestSafetyTimeout);

        var result = await runner.RunAsync(ShellRequest("echo metalforge"), progress: null, safety.Token);

        Assert.True(result.Succeeded, result.Describe());
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("metalforge", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(result.TimedOut);
        Assert.False(result.Canceled);
    }

    [Fact]
    public async Task NonZeroExitCode_IsReportedWithoutThrowing()
    {
        var runner = new ProcessRunner();
        using var safety = new CancellationTokenSource(TestSafetyTimeout);

        var result = await runner.RunAsync(ShellRequest("exit 3"), progress: null, safety.Token);

        // 失败通过返回值表达，不抛异常：调用方统一转成诊断。
        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.FailedToStart);
    }

    [Fact]
    public async Task MissingExecutable_IsReportedAsFailedToStartNotException()
    {
        var runner = new ProcessRunner();
        using var safety = new CancellationTokenSource(TestSafetyTimeout);

        var result = await runner.RunAsync(
            new ProcessRequest { FileName = "metalforge-tool-that-does-not-exist-9f3a" },
            progress: null,
            safety.Token);

        Assert.True(result.FailedToStart);
        Assert.Null(result.ExitCode);
        Assert.NotNull(result.StartupException);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DiagnosticSink_ReceivesStartupFailureDiagnostic()
    {
        var collected = new List<Diagnostic>();
        var runner = new ProcessRunner(collected.Add);
        using var safety = new CancellationTokenSource(TestSafetyTimeout);

        var result = await runner.RunAsync(
            new ProcessRequest { FileName = "metalforge-missing-tool-7b2c" },
            progress: null,
            safety.Token);

        Assert.True(result.FailedToStart);
        var diagnostic = Assert.Single(collected);
        Assert.Equal("MFPROC010", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.NotNull(diagnostic.Exception);
        Assert.NotNull(diagnostic.Hint);
    }

    [Fact]
    public async Task LargeOutput_DoesNotDeadlock()
    {
        // 这段命令会产生远超管道缓冲区（通常 4KB）的输出。
        // 若实现没有异步读取 stdout/stderr，子进程会阻塞在写管道上，
        // 父进程等待退出 → 永久死锁。该测试就是这条纪律的守卫。
        var runner = new ProcessRunner();
        using var safety = new CancellationTokenSource(TestSafetyTimeout);

        var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "for /L %i in (1,1,4000) do @echo line-%i-padding-padding-padding-padding"
            : "i=1; while [ $i -le 4000 ]; do echo line-$i-padding-padding-padding-padding; i=$((i+1)); done";

        var result = await runner.RunAsync(ShellRequest(command), progress: null, safety.Token);

        Assert.True(result.Succeeded, result.Describe());
        var lineCount = result.Lines.Count(line => !line.IsError);
        Assert.True(lineCount >= 4000, $"期望至少 4000 行输出，实际 {lineCount} 行");
    }

    [Fact]
    public async Task ProgressCallback_ReceivesLines()
    {
        var runner = new ProcessRunner();
        using var safety = new CancellationTokenSource(TestSafetyTimeout);
        var collected = new List<ProcessOutputLine>();
        var progress = new Progress<ProcessOutputLine>(collected.Add);

        var result = await runner.RunAsync(ShellRequest("echo first & echo second"), progress, safety.Token);

        Assert.True(result.Succeeded, result.Describe());

        // Progress<T> 通过同步上下文投递，等待它排空而不是立即断言。
        await WaitUntilAsync(() => collected.Count >= 2, TimeSpan.FromSeconds(5));
        Assert.True(collected.Count >= 2, $"期望至少 2 行回调，实际 {collected.Count}");
    }

    [Fact]
    public async Task Timeout_TerminatesProcessAndMarksTimedOut()
    {
        var runner = new ProcessRunner();
        using var safety = new CancellationTokenSource(TestSafetyTimeout);

        var stopwatch = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            ShellRequest(LongRunningCommand, TimeSpan.FromMilliseconds(800)),
            progress: null,
            safety.Token);
        stopwatch.Stop();

        Assert.True(result.TimedOut, result.Describe());
        Assert.False(result.Canceled);
        Assert.False(result.Succeeded);

        // 关键：必须在超时后很快返回，而不是等子进程自然结束（30 秒）。
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"超时后耗时过长：{stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Cancellation_TerminatesProcessAndMarksCanceled()
    {
        var runner = new ProcessRunner();
        using var cancellation = new CancellationTokenSource();

        var stopwatch = Stopwatch.StartNew();
        var runTask = runner.RunAsync(ShellRequest(LongRunningCommand), progress: null, cancellation.Token);

        await Task.Delay(400);
        await cancellation.CancelAsync();

        var result = await runTask;
        stopwatch.Stop();

        Assert.True(result.Canceled, result.Describe());
        Assert.False(result.TimedOut);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"取消后耗时过长：{stopwatch.Elapsed}");
    }

    [Fact]
    public async Task ArgumentsWithSpacesAndQuotes_ArePassedVerbatim()
    {
        // 参数列表化的意义：含空格的路径与引号不会被 shell 拆开。
        var runner = new ProcessRunner();
        using var safety = new CancellationTokenSource(TestSafetyTimeout);

        var tempFile = Path.Combine(Path.GetTempPath(), $"metalforge arg test {Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, "payload", safety.Token);

        try
        {
            var result = await runner.RunAsync(
                new ProcessRequest
                {
                    FileName = ShellPath,
                    Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        ? ["/c", "type", tempFile]
                        : ["-c", "cat \"$1\"", "sh", tempFile],
                    Timeout = TimeSpan.FromSeconds(10),
                },
                progress: null,
                safety.Token);

            Assert.True(result.Succeeded, result.Describe());
            Assert.Contains("payload", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task StandardInput_IsDeliveredToProcess()
    {
        var runner = new ProcessRunner();
        using var safety = new CancellationTokenSource(TestSafetyTimeout);

        var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "findstr metal" : "grep metal";

        var result = await runner.RunAsync(
            new ProcessRequest
            {
                FileName = ShellPath,
                Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ["/c", command] : ["-c", command],
                StandardInput = "nothing here\nmetal forge\n",
                Timeout = TimeSpan.FromSeconds(10),
            },
            progress: null,
            safety.Token);

        Assert.True(result.Succeeded, result.Describe());
        Assert.Contains("metal forge", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkingDirectory_IsHonored()
    {
        var runner = new ProcessRunner();
        using var safety = new CancellationTokenSource(TestSafetyTimeout);

        var tempDirectory = Path.Combine(Path.GetTempPath(), "metalforge-wd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cd" : "pwd";

            var result = await runner.RunAsync(
                ShellRequest(command, workingDirectory: tempDirectory),
                progress: null,
                safety.Token);

            Assert.True(result.Succeeded, result.Describe());
            Assert.Contains(
                Path.GetFileName(tempDirectory),
                result.StandardOutput,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Stderr_IsCapturedSeparately()
    {
        var runner = new ProcessRunner();
        using var safety = new CancellationTokenSource(TestSafetyTimeout);

        var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "echo to-stdout & echo to-stderr 1>&2"
            : "echo to-stdout; echo to-stderr 1>&2";

        var result = await runner.RunAsync(ShellRequest(command), progress: null, safety.Token);

        Assert.True(result.Succeeded, result.Describe());
        Assert.Contains("to-stdout", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("to-stderr", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(result.Lines, line => line.IsError);
    }

    [Fact]
    public async Task Utf8Output_IsDecodedCorrectly()
    {
        // 编码纪律：标准流必须按 UTF-8 解码，否则中文输出会变成乱码。
        //
        // 关键点：不能用 "cmd /c powershell -Command ..." 这类写法来测编码 ——
        // 非 ASCII 的**命令行参数**本身就要经过 cmd 的 ANSI 编码转换，一旦损坏，
        // 测的就不再是"我们的解码是否正确"，而是"命令行能否传中文"。
        // 因此这里用 -EncodedCommand（Base64 传输，纯 ASCII 命令行），
        // 让中文只出现在子进程的输出里。
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var runner = new ProcessRunner();
        using var safety = new CancellationTokenSource(TestSafetyTimeout);

        const string expected = "内核已启动 · 串口就绪";
        var script = $"$OutputEncoding = [System.Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); Write-Output '{expected}'";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var result = await runner.RunAsync(
            ShellRequest($"powershell -NoProfile -NonInteractive -EncodedCommand {encodedCommand}"),
            progress: null,
            safety.Token);

        Assert.True(result.Succeeded, result.Describe());
        Assert.Contains(expected, result.CombinedOutput, StringComparison.Ordinal);

        // 再确认一遍原始字节确实是 UTF-8：若实现改用系统默认编码，上面会得到乱码。
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(result.CombinedOutput);
        Assert.Contains(
            Convert.ToHexString(expectedBytes),
            Convert.ToHexString(actualBytes),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractiveProcess_StreamsOutputAndExits()
    {
        var runner = new ProcessRunner();
        using var safety = new CancellationTokenSource(TestSafetyTimeout);

        await using var process = runner.StartInteractive(ShellRequest("echo interactive-ok"));
        var lines = new List<string>();
        process.OutputReceived += (_, line) => lines.Add(line.Text);

        var exitCode = await process.WaitForExitAsync(safety.Token);
        await WaitUntilAsync(() => lines.Count > 0, TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);
        Assert.Contains(lines, line => line.Contains("interactive-ok", StringComparison.Ordinal));
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task InteractiveProcess_DisposeTerminatesLongRunningChild()
    {
        // 长驻进程纪律：释放句柄必须终止进程。QEMU/clangd 残留会持续占用 CPU 与端口。
        var runner = new ProcessRunner();

        var process = runner.StartInteractive(ShellRequest(LongRunningCommand));
        var processId = process.ProcessId;
        Assert.True(processId > 0);

        await Task.Delay(300);
        Assert.False(process.HasExited);

        await process.DisposeAsync();

        Assert.True(process.HasExited, "释放后进程应已终止");
        await WaitUntilAsync(() => !IsProcessAlive(processId), TimeSpan.FromSeconds(10));
        Assert.False(IsProcessAlive(processId), $"进程 {processId} 在释放后仍然存活（进程树终止失效）");
    }

    [Fact]
    public async Task InteractiveProcess_CanWriteToStandardInput()
    {
        var runner = new ProcessRunner();
        using var safety = new CancellationTokenSource(TestSafetyTimeout);

        var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "findstr metal" : "grep metal";

        await using var process = runner.StartInteractive(new ProcessRequest
        {
            FileName = ShellPath,
            Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ["/c", command] : ["-c", command],
        });

        var lines = new List<string>();
        process.OutputReceived += (_, line) => lines.Add(line.Text);

        await process.WriteLineAsync("metal forge", safety.Token);
        process.StandardInput?.Close();

        var exitCode = await process.WaitForExitAsync(safety.Token);
        await WaitUntilAsync(() => lines.Count > 0, TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);
        Assert.Contains(lines, line => line.Contains("metal forge", StringComparison.Ordinal));
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }
    }
}
