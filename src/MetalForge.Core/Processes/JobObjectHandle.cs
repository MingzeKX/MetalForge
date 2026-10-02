using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MetalForge.Core.Processes;

/// <summary>
/// Windows Job Object 封装：把一个或多个进程放进同一个 Job，
/// 并在 Job 句柄关闭时由内核终止其中全部进程。
///
/// 为什么需要它（而不是只用 <c>Process.Kill(entireProcessTree: true)</c>）：
/// 进程树遍历在两种情况下会漏掉子孙进程 ——
///   1) 中间层子进程自己也处于一个设置了 KILL_ON_JOB_CLOSE 的 Job 中；
///   2) 父进程在遍历开始前就已退出，树结构丢失。
/// QEMU 会派生辅助进程，残留的 QEMU 会持续占用 CPU 与端口；这类问题必须在结构上消除，
/// 而不是靠遍历的尽力而为。
///
/// 非 Windows 平台返回 null，调用方退化为遍历终止。
/// </summary>
internal sealed class JobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;

    /// <summary>
    /// 无参构造函数必须与类型可见性一致（CA1419）。P/Invoke 返回 SafeHandle 派生类型时
    /// 由运行时负责实例化，此构造函数不会被业务代码调用。
    /// </summary>
    internal JobObjectHandle()
        : base(ownsHandle: true)
    {
    }

    /// <summary>尝试创建 Job 并配置为"句柄关闭即终止全部成员"。不可用时返回 null。</summary>
    public static JobObjectHandle? TryCreate(out string? failureReason)
    {
        failureReason = null;

        if (!OperatingSystem.IsWindows())
        {
            failureReason = "当前平台不支持 Job Object。";
            return null;
        }

        // 用中间变量 + 显式释放路径，让"所有权何时转移"对分析器（CA2000）可见：
        // 只有全部初始化步骤都成功，句柄才被返回给调用方；任何提前返回都先释放。
        JobObjectHandle? result = null;
        // CA2000 误报：分析器不认为 P/Invoke 返回的 SafeHandle 派生类型是"已拥有"的资源，
        // 因此看不到下面 finally 中覆盖全部提前返回路径的释放逻辑。
        // 实际的所有权规则是：只有初始化全部成功时句柄才交给调用方（result != null），
        // 其余任何路径都在 finally 中释放。抑制范围仅限这一行。
#pragma warning disable CA2000 // Dispose objects before losing scope
        var handle = CreateJobObject(IntPtr.Zero, null);
#pragma warning restore CA2000
        try
        {
            if (handle.IsInvalid)
            {
                failureReason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return null;
            }

            var limits = new JobObjectExtendedLimitInformationStructure
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };

            var size = Marshal.SizeOf<JobObjectExtendedLimitInformationStructure>();
            var limitsPointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, limitsPointer, fDeleteOld: false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, limitsPointer, (uint)size))
                {
                    failureReason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(limitsPointer);
            }

            result = handle;
            return result;
        }
        finally
        {
            // 所有权已转移给 result 时不释放；否则（含所有提前返回）在此释放。
            if (result is null)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>把进程加入 Job。失败时返回 false（调用方退化为遍历终止，不视为致命错误）。</summary>
    public bool TryAssignProcess(IntPtr processHandle, out string? failureReason)
    {
        failureReason = null;

        if (IsInvalid || IsClosed)
        {
            failureReason = "Job 句柄不可用。";
            return false;
        }

        if (!AssignProcessToJobObject(this, processHandle))
        {
            failureReason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        return true;
    }

    /// <summary>立即终止 Job 中全部进程。</summary>
    public bool TryTerminateAll(out string? failureReason)
    {
        failureReason = null;

        if (IsInvalid || IsClosed)
        {
            failureReason = "Job 句柄不可用。";
            return false;
        }

        if (!TerminateJobObject(this, 0))
        {
            failureReason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        return true;
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern JobObjectHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(JobObjectHandle job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(JobObjectHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(JobObjectHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStructure
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    /// <summary>供诊断：进程操作所需的访问权限（保留常量以便将来按需使用）。</summary>
    public const uint RequiredProcessAccess = ProcessSetQuota | ProcessTerminate;
}
