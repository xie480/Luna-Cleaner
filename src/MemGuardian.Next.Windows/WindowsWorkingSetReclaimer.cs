using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MemGuardian.Next.Core;
using MemGuardian.Next.Windows.Native;

namespace MemGuardian.Next.Windows;

/// <summary>Revalidates process creation time and asks documented PSAPI to trim resident pages.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWorkingSetReclaimer : IWorkingSetReclaimer
{
    private readonly string _currentUserSid;
    private readonly uint _currentSessionId;

    /// <summary>Captures the interactive identity whose own processes are eligible for trimming.</summary>
    public WindowsWorkingSetReclaimer()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _currentUserSid = identity.User?.Value ?? string.Empty;
        using var process = Process.GetCurrentProcess();
        _currentSessionId = (uint)process.SessionId;
    }

    /// <summary>Calls EmptyWorkingSet only on the exact PID instance selected by policy.</summary>
    public ReclaimerResult TryTrim(ProcessIdentity identity)
    {
        if (identity.ProcessId <= 0 || identity.ProcessId == Environment.ProcessId)
            return new ReclaimerResult(false, "Refused system or self process.");

        using var process = NativeMethods.OpenProcess(NativeConstants.ProcessQueryLimitedInformation |
            NativeConstants.ProcessSetQuota, false, (uint)identity.ProcessId);
        if (process.IsInvalid)
            return new ReclaimerResult(false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        if (!NativeMethods.GetProcessTimes(process, out var creationTime, out _, out _, out _))
            return new ReclaimerResult(false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        if (creationTime.ToInt64() != identity.CreationTimeFileTime)
            return new ReclaimerResult(false, "PID was reused; process identity changed.");
        if (!NativeMethods.ProcessIdToSessionId((uint)identity.ProcessId, out var targetSession) ||
            targetSession != _currentSessionId)
            return new ReclaimerResult(false, "Target is not in the current user session.");
        if (!NativeMethods.IsProcessCritical(process, out var critical) || critical)
            return new ReclaimerResult(false, "Target is critical or its critical-process status is unknown.");
        if (!NativeMethods.OpenProcessToken(process, NativeConstants.TokenQuery, out SafeAccessTokenHandle token))
            return new ReclaimerResult(false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        using (token)
        {
            using var targetIdentity = CreateWindowsIdentity(token);
            if (!StringComparer.OrdinalIgnoreCase.Equals(targetIdentity.User?.Value, _currentUserSid))
                return new ReclaimerResult(false, "Target owner changed or is not the current user.");
        }
        if (WindowsForegroundTracker.GetForegroundProcessId() == identity.ProcessId)
            return new ReclaimerResult(false, "Target became the foreground process before trim.");
        if (!NativeMethods.EmptyWorkingSet(process))
            return new ReclaimerResult(false, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        return new ReclaimerResult(true, null);
    }

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
}
