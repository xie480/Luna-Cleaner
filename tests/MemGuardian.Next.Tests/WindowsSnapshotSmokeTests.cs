using MemGuardian.Next.Core;
using MemGuardian.Next.Windows;
using Xunit;

namespace MemGuardian.Next.Tests;

/// <summary>Performs a read-only Windows API smoke test without invoking process recovery.</summary>
public sealed class WindowsSnapshotSmokeTests
{
    /// <summary>Checks system/process counters and SafeHandle-backed owner collection on Windows.</summary>
    [Fact]
    public async Task WindowsProviderCollectsSystemAndCurrentProcessMetrics()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var provider = new WindowsSystemSnapshotProvider(() => new ReclaimState(), new GuardianOptions());
        var snapshot = await provider.CaptureAsync(refreshProcesses: true);

        Assert.True(snapshot.Memory.TotalPhysicalBytes > 0);
        Assert.True(snapshot.Memory.AvailablePhysicalBytes <= snapshot.Memory.TotalPhysicalBytes);
        Assert.True(snapshot.Memory.CommitLimitBytes > 0);
        var current = Assert.Single(snapshot.Processes, process =>
            process.Identity.ProcessId == Environment.ProcessId);
        Assert.True(current.WorkingSetBytes > 0);
        Assert.True(current.PrivateCommitBytes > 0);
        Assert.True(current.ThreadCount > 0);
    }
}
