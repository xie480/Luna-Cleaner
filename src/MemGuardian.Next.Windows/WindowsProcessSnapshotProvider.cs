using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MemGuardian.Next.Core;
using MemGuardian.Next.Windows.Native;

namespace MemGuardian.Next.Windows;

/// <summary>Collects per-process memory, CPU, I/O, owner, session and foreground metadata without WMI.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessSnapshotProvider
{
    private readonly Func<ReclaimState> _getState;
    private readonly string _currentUserSid;
    private readonly int _currentSessionId;
    private readonly Dictionary<ProcessIdentity, ProcessRateSample> _previous = new();
    private readonly int _maximumTracked;

    /// <summary>Creates a collector bound to the current interactive identity.</summary>
    public WindowsProcessSnapshotProvider(Func<ReclaimState> getState, int maximumTrackedProcesses)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("MemGuardian.Next requires Windows.");
        _getState = getState;
        _maximumTracked = Math.Clamp(maximumTrackedProcesses, 1, 2048);
        using var currentIdentity = WindowsIdentity.GetCurrent();
        _currentUserSid = currentIdentity.User?.Value ?? string.Empty;
        using var currentProcess = Process.GetCurrentProcess();
        _currentSessionId = currentProcess.SessionId;
    }

    /// <summary>Reads the process table once and skips transient per-process failures.</summary>
    public IReadOnlyList<ProcessSnapshot> Collect(DateTimeOffset now)
    {
        var results = new List<ProcessSnapshot>();
        var observed = new HashSet<ProcessIdentity>();
        var state = _getState();
        var foregroundPid = WindowsForegroundTracker.GetForegroundProcessId();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var processName = process.ProcessName;
                    var sessionId = process.SessionId;
                    var snapshot = CaptureOne(process, processName, sessionId, state, now, foregroundPid);
                    if (snapshot is null) continue;
                    observed.Add(snapshot.Identity);
                    results.Add(snapshot);
                }
                catch (Exception exception) when (IsTransientProcessError(exception))
                {
                    // Processes can exit or deny access while the list is being read.
                }
            }
        }

        foreach (var identity in _previous.Keys.Where(identity => !observed.Contains(identity)).ToArray())
            _previous.Remove(identity);
        TrimRateHistory();
        return results;
    }

    private ProcessSnapshot? CaptureOne(Process process, string processName, int sessionId, ReclaimState state,
        DateTimeOffset now, int? foregroundPid)
    {
        using var handle = NativeMethods.OpenProcess(NativeConstants.ProcessQueryLimitedInformation |
            NativeConstants.ProcessSetQuota, false, (uint)process.Id);
        if (handle.IsInvalid)
        {
            return CreateUnavailable(process.Id, processName, sessionId,
                new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        if (!NativeMethods.GetProcessTimes(handle, out var creationTime, out _, out var kernelTime, out var userTime))
            return null;
        var identity = new ProcessIdentity(process.Id, creationTime.ToInt64());
        var counters = new ProcessMemoryCountersEx { Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>() };
        if (!NativeMethods.GetProcessMemoryInfo(handle, ref counters, counters.Size))
        {
            return CreateUnavailable(process.Id, processName, sessionId,
                new Win32Exception(Marshal.GetLastWin32Error()).Message, identity, state);
        }

        var ownerAvailable = GetProcessOwnerSid(handle, out var sid, out _);
        var ioAvailable = NativeMethods.GetProcessIoCounters(handle, out var ioCounters);
        var criticalStatusKnown = NativeMethods.IsProcessCritical(handle, out var isCritical);
        var cpuTicks = kernelTime.ToInt64() + userTime.ToInt64();
        var transferBytes = AddSaturating(AddSaturating(ioCounters.ReadTransferCount, ioCounters.WriteTransferCount),
            ioCounters.OtherTransferCount);
        var (cpu, ioRate) = GetRates(identity, now, cpuTicks, transferBytes, ioAvailable);
        var currentUser = ownerAvailable && StringComparer.OrdinalIgnoreCase.Equals(sid, _currentUserSid);
        var lastUsed = LookupLastUse(state, identity);
        var foreground = foregroundPid == process.Id;
        return new ProcessSnapshot(identity, processName, (ulong)counters.WorkingSetSize,
            (ulong)counters.PrivateUsage, cpu, ioRate, process.Threads.Count, process.HandleCount,
            ownerAvailable ? sid : null, sessionId, currentUser, sessionId == _currentSessionId, foreground,
            lastUsed, CanTrim: criticalStatusKnown && !isCritical,
            CollectionError: !criticalStatusKnown ? "Cannot verify Windows critical-process status" :
                isCritical ? "Windows critical process" : null);
    }

    private ProcessSnapshot CreateUnavailable(int pid, string name, int? sessionId, string error,
        ProcessIdentity? knownIdentity = null, ReclaimState? state = null)
    {
        var identity = knownIdentity ?? new ProcessIdentity(pid, 0);
        var lastUsed = knownIdentity is not null && state is not null ? LookupLastUse(state, identity) : null;
        return new ProcessSnapshot(identity, name, 0, 0, null, null, 0, 0, null, sessionId,
            false, false, false, lastUsed, false, error);
    }

    private (double? CpuPercent, double? IoBytesPerSecond) GetRates(ProcessIdentity identity, DateTimeOffset now,
        long cpuTicks, ulong ioBytes, bool ioAvailable)
    {
        double? cpu = null;
        double? io = null;
        if (_previous.TryGetValue(identity, out var previous))
        {
            var elapsed = (now - previous.CapturedAt).TotalSeconds;
            if (elapsed > 0)
            {
                if (cpuTicks >= previous.CpuTicks)
                    cpu = (cpuTicks - previous.CpuTicks) / 10_000_000d / elapsed / Environment.ProcessorCount * 100;
                if (ioAvailable && ioBytes >= previous.IoBytes)
                    io = (ioBytes - previous.IoBytes) / elapsed;
            }
        }

        _previous[identity] = new ProcessRateSample(now, cpuTicks, ioBytes);
        return (cpu, io);
    }

    private static DateTimeOffset? LookupLastUse(ReclaimState state, ProcessIdentity identity) =>
        state.LastForegroundUse.TryGetValue(identity.ToKey(), out var value) ? value : null;

    private static ulong AddSaturating(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static WindowsIdentity CreateWindowsIdentity(SafeAccessTokenHandle token)
    {
        var addedReference = false;
        try
        {
            token.DangerousAddRef(ref addedReference);
            return new WindowsIdentity(token.DangerousGetHandle());
        }
        finally
        {
            if (addedReference) token.DangerousRelease();
        }
    }

    private static bool GetProcessOwnerSid(SafeProcessHandle process, out string? sid, out string? error)
    {
        sid = null;
        error = null;
        if (!NativeMethods.OpenProcessToken(process, NativeConstants.TokenQuery, out var token))
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        using (token)
        {
            try
            {
                using var identity = CreateWindowsIdentity(token);
                sid = identity.User?.Value;
                return sid is not null;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or Win32Exception or ArgumentException)
            {
                error = exception.Message;
                return false;
            }
        }
    }

    private void TrimRateHistory()
    {
        while (_previous.Count > _maximumTracked)
        {
            var oldest = _previous.MinBy(pair => pair.Value.CapturedAt).Key;
            _previous.Remove(oldest);
        }
    }

    private static bool IsTransientProcessError(Exception exception) => exception is
        InvalidOperationException or Win32Exception or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    private sealed record ProcessRateSample(DateTimeOffset CapturedAt, long CpuTicks, ulong IoBytes);
}
