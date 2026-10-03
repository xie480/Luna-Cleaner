using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MemGuardian.Next.Windows.Native;

/// <summary>Documented Win32/PSAPI/PDH imports used by the Windows adapter.</summary>
internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("psapi.dll", SetLastError = true, EntryPoint = "GetPerformanceInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPerformanceInfo(ref PerformanceInformation information, uint size);

    [DllImport("psapi.dll", SetLastError = true, EntryPoint = "GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref ProcessMemoryCountersEx counters, uint size);

    [DllImport("psapi.dll", SetLastError = true, EntryPoint = "EmptyWorkingSet")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyWorkingSet(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out NativeFileTime creationTime,
        out NativeFileTime exitTime, out NativeFileTime kernelTime, out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", ExactSpelling = true)]
    internal static extern int PdhOpenQueryW([MarshalAs(UnmanagedType.LPWStr)] string? dataSource, nuint userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", ExactSpelling = true)]
    internal static extern int PdhAddEnglishCounterW(SafePdhQueryHandle query, [MarshalAs(UnmanagedType.LPWStr)] string counterPath,
        nuint userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    internal static extern int PdhCollectQueryData(SafePdhQueryHandle query);

    [DllImport("pdh.dll")]
    internal static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint counterType,
        out PdhFormattedCounterValue value);

    [DllImport("pdh.dll")]
    internal static extern int PdhCloseQuery(IntPtr query);
}

/// <summary>MEMORYSTATUSEX layout used by GlobalMemoryStatusEx.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatusEx
{
    internal uint Length;
    internal uint MemoryLoad;
    internal ulong TotalPhysical;
    internal ulong AvailablePhysical;
    internal ulong TotalPageFile;
    internal ulong AvailablePageFile;
    internal ulong TotalVirtual;
    internal ulong AvailableVirtual;
    internal ulong AvailableExtendedVirtual;
}

/// <summary>PERFORMANCE_INFORMATION layout; SIZE_T fields use native-sized integers.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PerformanceInformation
{
    internal uint Size;
    internal nuint CommitTotal;
    internal nuint CommitLimit;
    internal nuint CommitPeak;
    internal nuint PhysicalTotal;
    internal nuint PhysicalAvailable;
    internal nuint SystemCache;
    internal nuint KernelTotal;
    internal nuint KernelPaged;
    internal nuint KernelNonpaged;
    internal nuint PageSize;
    internal uint HandleCount;
    internal uint ProcessCount;
    internal uint ThreadCount;
}

/// <summary>PROCESS_MEMORY_COUNTERS_EX layout; PrivateUsage is process Private Commit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessMemoryCountersEx
{
    internal uint Size;
    internal uint PageFaultCount;
    internal nuint PeakWorkingSetSize;
    internal nuint WorkingSetSize;
    internal nuint QuotaPeakPagedPoolUsage;
    internal nuint QuotaPagedPoolUsage;
    internal nuint QuotaPeakNonPagedPoolUsage;
    internal nuint QuotaNonPagedPoolUsage;
    internal nuint PagefileUsage;
    internal nuint PeakPagefileUsage;
    internal nuint PrivateUsage;
}

/// <summary>FILETIME as two DWORDs returned by GetProcessTimes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeFileTime
{
    internal uint Low;
    internal uint High;
    internal readonly long ToInt64() => unchecked((long)(((ulong)High << 32) | Low));
}

/// <summary>IO_COUNTERS used to derive per-process read/write transfer rates.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    internal ulong ReadOperationCount;
    internal ulong WriteOperationCount;
    internal ulong OtherOperationCount;
    internal ulong ReadTransferCount;
    internal ulong WriteTransferCount;
    internal ulong OtherTransferCount;
}

/// <summary>PDH formatted value union. The x64 process layout has the value at offset 8.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct PdhFormattedCounterValue
{
    [FieldOffset(0)] internal uint Status;
    [FieldOffset(8)] internal double DoubleValue;
}

/// <summary>Owns one PDH query; closing it releases every counter associated with that query.</summary>
internal sealed class SafePdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafePdhQueryHandle() : base(true) { }
    internal SafePdhQueryHandle(IntPtr value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() => NativeMethods.PdhCloseQuery(handle) == NativeConstants.ErrorSuccess;
}
