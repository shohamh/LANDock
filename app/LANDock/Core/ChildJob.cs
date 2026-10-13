using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LANDock.Core;

/// <summary>
/// A Windows job object: the processes put in it end when the job's last handle closes, which is when this program ends. A helper
/// (vddtool) put in it cannot outlive the program that started it, so a helper that hangs does not stay behind holding its files.
/// </summary>
public sealed class ChildJob : IDisposable
{
    /// <summary>The job of this program's helpers: it lives as long as the program does.</summary>
    public static readonly ChildJob Helpers = new();

    private const int ExtendedLimitInfoClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _job;

    public ChildJob()
    {
        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job == IntPtr.Zero) return;
        var info = new JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        SetInformationJobObject(_job, ExtendedLimitInfoClass, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>());
    }

    /// <summary>Puts a started process in the job. A process that cannot be added (for example one already in another job) is left as it is.</summary>
    public bool Add(Process p)
    {
        if (_job == IntPtr.Zero) return false;
        try { return AssignProcessToJobObject(_job, p.Handle); }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
