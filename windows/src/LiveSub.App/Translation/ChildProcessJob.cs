using System.Diagnostics;
using System.Runtime.InteropServices;
using LiveSub.App.Services;

namespace LiveSub.App.Translation;

/// <summary>
/// 把子进程（claude 及其启动的进程）放进一个“随本程序关闭而结束”的作业对象：
/// 本程序退出或崩溃时，Windows 自动结束这些子进程，不会留下孤儿进程。
/// </summary>
internal static partial class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
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
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObject(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    /// <summary>作业对象的句柄在程序运行期间一直保持打开；进程结束时系统关闭它并结束其中的进程。</summary>
    private static readonly Lazy<IntPtr> Job = new(CreateKillOnCloseJob);

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            AppLog.Error($"job: CreateJobObject failed ({Marshal.GetLastWin32Error()})");
            return IntPtr.Zero;
        }
        var info = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            AppLog.Error($"job: SetInformationJobObject failed ({Marshal.GetLastWin32Error()})");
        }
        return job;
    }

    public static void Add(Process process)
    {
        var job = Job.Value;
        if (job == IntPtr.Zero) return;
        try
        {
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                AppLog.Error($"job: AssignProcessToJobObject failed ({Marshal.GetLastWin32Error()})");
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已经退出。
        }
    }
}
