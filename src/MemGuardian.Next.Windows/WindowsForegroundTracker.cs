using MemGuardian.Next.Core;
using MemGuardian.Next.Windows.Native;
using System.Runtime.Versioning;

namespace MemGuardian.Next.Windows;

/// <summary>Polls the documented foreground window API and records process-instance usage.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsForegroundTracker
{
    /// <summary>Returns the foreground window owner PID or null if no window is active.</summary>
    public static int? GetForegroundProcessId()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero || NativeMethods.GetWindowThreadProcessId(window, out var processId) == 0)
            return null;
        return processId <= int.MaxValue ? (int)processId : null;
    }

    /// <summary>Resolves the foreground PID to an identity containing its creation time.</summary>
    public static ProcessIdentity? GetForegroundIdentity()
    {
        var pid = GetForegroundProcessId();
        if (pid is null or <= 0) return null;
        using var process = NativeMethods.OpenProcess(NativeConstants.ProcessQueryLimitedInformation, false, (uint)pid.Value);
        if (process.IsInvalid || !NativeMethods.GetProcessTimes(process, out var creation, out _, out _, out _)) return null;
        return new ProcessIdentity(pid.Value, creation.ToInt64());
    }
}
