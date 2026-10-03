using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MemGuardian.Next.Core;
using MemGuardian.Next.Windows.Native;

namespace MemGuardian.Next.Windows;

/// <summary>Combines GlobalMemoryStatusEx, GetPerformanceInfo, PDH and a cached process inventory.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemSnapshotProvider : ISystemSnapshotProvider, IDisposable
{
    private readonly PdhMemoryCounters _pdh;
    private readonly WindowsProcessSnapshotProvider _processProvider;
    private readonly List<string> _counterErrors = new();
    private IReadOnlyList<ProcessSnapshot> _lastProcesses = Array.Empty<ProcessSnapshot>();

    /// <summary>Creates a Windows snapshot source and retains PDH query handles until disposal.</summary>
    public WindowsSystemSnapshotProvider(Func<ReclaimState> getState, GuardianOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("MemGuardian.Next requires Windows.");
        _pdh = new PdhMemoryCounters(_counterErrors);
        _processProvider = new WindowsProcessSnapshotProvider(getState, options.MaximumTrackedProcesses);
    }

    /// <summary>Captures system metrics; process enumeration is refreshed only on request.</summary>
    public ValueTask<SystemSnapshot> CaptureAsync(bool refreshProcesses, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        if (refreshProcesses) _lastProcesses = _processProvider.Collect(now);
        var memory = CaptureMetrics(now, _lastProcesses);
        return ValueTask.FromResult(new SystemSnapshot(memory, _lastProcesses));
    }

    /// <summary>Provider diagnostics for unavailable English counters.</summary>
    public IReadOnlyList<string> CounterErrors => _counterErrors;

    /// <summary>Closes the PDH query and its counter handles.</summary>
    public void Dispose() => _pdh.Dispose();

    private SystemMemoryMetrics CaptureMetrics(DateTimeOffset now, IReadOnlyList<ProcessSnapshot> processes)
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalMemoryStatusEx failed.");
        var performance = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (!NativeMethods.GetPerformanceInfo(ref performance, performance.Size))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetPerformanceInfo failed.");

        var pageSize = (ulong)performance.PageSize;
        var pdh = _pdh.Collect();
        var aggregateWorkingSet = processes.Aggregate(0UL, (total, process) => total + process.WorkingSetBytes);
        return new SystemMemoryMetrics(now, status.TotalPhysical, status.AvailablePhysical,
            (ulong)performance.CommitTotal * pageSize, (ulong)performance.CommitLimit * pageSize,
            (ulong)performance.SystemCache * pageSize, (ulong)performance.KernelPaged * pageSize,
            (ulong)performance.KernelNonpaged * pageSize, pdh.PageReadsPerSecond, pdh.PagesInputPerSecond,
            pdh.PagefileUsagePercent, aggregateWorkingSet);
    }
}
