using MemGuardian.Next.Core;
using Xunit;

namespace MemGuardian.Next.Tests;

/// <summary>Validates multi-signal pressure diagnosis and bounded leak trend rules.</summary>
public sealed class DiagnosisTests
{
    /// <summary>正常物理可用量和 Commit 不应报告压力。</summary>
    [Fact]
    public void NormalMemoryDoesNotReportPressure()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Sample(now, available: 16, commit: 18, limit: 64, processWorkingSet: 8);
        var result = new DiagnosisEngine().Analyze(snapshot, Array.Empty<HistoryEntry>(), new GuardianOptions());
        Assert.Equal(PressureLevel.Normal, result.Level);
        Assert.Empty(result.Flags);
    }

    /// <summary>高物理驻留且低 Commit 应定位为 Working Set 压力。</summary>
    [Fact]
    public void HighPhysicalUseWithLowCommitIsWorkingSetPressure()
    {
        var snapshot = Sample(DateTimeOffset.UtcNow, available: 2, commit: 12, limit: 64, processWorkingSet: 28);
        var result = new DiagnosisEngine().Analyze(snapshot, Array.Empty<HistoryEntry>(), new GuardianOptions());
        Assert.Contains(DiagnosticFlag.WorkingSetPressure, result.Flags);
        Assert.DoesNotContain(DiagnosticFlag.CommitPressure, result.Flags);
        Assert.Equal(PressureLevel.Pressure, result.Level);
    }

    /// <summary>接近 Commit Limit 时应独立报告 Commit 压力。</summary>
    [Fact]
    public void HighCommitIsDiagnosedSeparatelyFromPhysicalPressure()
    {
        var snapshot = Sample(DateTimeOffset.UtcNow, available: 8, commit: 63, limit: 64, processWorkingSet: 12);
        var result = new DiagnosisEngine().Analyze(snapshot, Array.Empty<HistoryEntry>(), new GuardianOptions());
        Assert.Contains(DiagnosticFlag.CommitPressure, result.Flags);
        Assert.Equal(PressureLevel.Critical, result.Level);
    }

    /// <summary>持续的 Page Reads/sec / Pages Input/sec 会形成独立 paging 诊断。</summary>
    [Fact]
    public void SustainedPagingRatesAreDiagnosedSeparately()
    {
        var now = DateTimeOffset.UtcNow;
        var history = new SystemHistoryBuffer(20, TimeSpan.FromMinutes(1));
        for (var index = 0; index < 3; index++)
        {
            var at = now - TimeSpan.FromSeconds((2 - index) * 5);
            var memory = Metrics(at, 4, 20, 64, 10) with
            {
                PageReadsPerSecond = 7,
                PagesInputPerSecond = 60
            };
            history.Add(new SystemSnapshot(memory, Array.Empty<ProcessSnapshot>()), 0);
        }

        var latest = history.Snapshot()[^1];
        var result = new DiagnosisEngine().Analyze(new SystemSnapshot(Metrics(latest.CapturedAt, 4, 20, 64, 10)
            with { PageReadsPerSecond = 7, PagesInputPerSecond = 60 }, Array.Empty<ProcessSnapshot>()),
            history.Snapshot(), new GuardianOptions());

        Assert.Contains(DiagnosticFlag.PagingPressure, result.Flags);
    }

    /// <summary>同一进程实例 Private Commit 长期单调增长时提示可能的进程泄漏。</summary>
    [Fact]
    public void SustainedProcessPrivateCommitGrowthSignalsPossibleLeak()
    {
        var now = DateTimeOffset.UtcNow;
        var history = new SystemHistoryBuffer(120, TimeSpan.FromMinutes(60));
        var identity = new ProcessIdentity(2100, 123456);
        for (var index = 0; index <= 5; index++)
        {
            var snapshot = Sample(now + TimeSpan.FromMinutes(index * 2), 12, 20, 64, 10,
                new[] { Process(identity, 100UL * MiB + (ulong)index * 60 * MiB) });
            history.Add(snapshot, 512);
        }
        var current = Sample(now + TimeSpan.FromMinutes(10), 12, 20, 64, 10,
            new[] { Process(identity, 400UL * MiB) });
        var result = new DiagnosisEngine().Analyze(current, history.Snapshot(), new GuardianOptions());
        Assert.Contains(DiagnosticFlag.PossibleProcessLeak, result.Flags);
    }

    /// <summary>平稳趋势末尾的单次跳涨不足以判定为持续进程泄漏。</summary>
    [Fact]
    public void OneTimeProcessCommitJumpDoesNotSignalSustainedLeak()
    {
        var now = DateTimeOffset.UtcNow;
        var history = new SystemHistoryBuffer(120, TimeSpan.FromMinutes(60));
        var identity = new ProcessIdentity(2110, 123457);
        for (var index = 0; index <= 5; index++)
        {
            var commit = index == 5 ? 500UL * MiB : 100UL * MiB;
            var snapshot = Sample(now + TimeSpan.FromMinutes(index * 2), 12, 20, 64, 10,
                new[] { Process(identity, commit) });
            history.Add(snapshot, 512);
        }

        var current = Sample(now + TimeSpan.FromMinutes(10), 12, 20, 64, 10,
            new[] { Process(identity, 500UL * MiB) });
        var result = new DiagnosisEngine().Analyze(current, history.Snapshot(), new GuardianOptions());

        Assert.DoesNotContain(DiagnosticFlag.PossibleProcessLeak, result.Flags);
    }

    /// <summary>Nonpaged Pool 长期增长应提示可能的驱动泄漏。</summary>
    [Fact]
    public void SustainedNonpagedPoolGrowthSignalsPossibleDriverLeak()
    {
        var now = DateTimeOffset.UtcNow;
        var history = new SystemHistoryBuffer(120, TimeSpan.FromMinutes(60));
        for (var index = 0; index <= 5; index++)
        {
            var metrics = Metrics(now + TimeSpan.FromMinutes(index * 2), 12, 20, 64, 10,
                nonpaged: 100UL * MiB + (ulong)index * 20 * MiB);
            history.Add(new SystemSnapshot(metrics, Array.Empty<ProcessSnapshot>()), 512);
        }
        var current = new SystemSnapshot(Metrics(now + TimeSpan.FromMinutes(10), 12, 20, 64, 10,
            nonpaged: 200UL * MiB), Array.Empty<ProcessSnapshot>());
        var result = new DiagnosisEngine().Analyze(current, history.Snapshot(), new GuardianOptions());
        Assert.Contains(DiagnosticFlag.PossibleDriverLeak, result.Flags);
        Assert.Contains(DiagnosticFlag.KernelPoolPressure, result.Flags);
    }

    /// <summary>Nonpaged Pool 单次跃升而非持续上升时不提示驱动泄漏。</summary>
    [Fact]
    public void OneTimeNonpagedPoolJumpDoesNotSignalSustainedDriverLeak()
    {
        var now = DateTimeOffset.UtcNow;
        var history = new SystemHistoryBuffer(120, TimeSpan.FromMinutes(60));
        for (var index = 0; index <= 5; index++)
        {
            var pool = index == 5 ? 300UL * MiB : 100UL * MiB;
            var metrics = Metrics(now + TimeSpan.FromMinutes(index * 2), 12, 20, 64, 10, nonpaged: pool);
            history.Add(new SystemSnapshot(metrics, Array.Empty<ProcessSnapshot>()), 512);
        }
        var current = new SystemSnapshot(Metrics(now + TimeSpan.FromMinutes(10), 12, 20, 64, 10,
            nonpaged: 300UL * MiB), Array.Empty<ProcessSnapshot>());

        var result = new DiagnosisEngine().Analyze(current, history.Snapshot(), new GuardianOptions());

        Assert.DoesNotContain(DiagnosticFlag.PossibleDriverLeak, result.Flags);
    }

    private static SystemSnapshot Sample(DateTimeOffset now, double available, double commit, double limit,
        double processWorkingSet, IReadOnlyList<ProcessSnapshot>? processes = null)
    {
        var values = processes ?? Array.Empty<ProcessSnapshot>();
        return new SystemSnapshot(Metrics(now, available, commit, limit, processWorkingSet), values);
    }

    private static SystemMemoryMetrics Metrics(DateTimeOffset now, double available, double commit, double limit,
        double processWorkingSet, ulong nonpaged = 128 * MiB) => new(now, 32 * GiB,
        ToBytes(available), ToBytes(commit), ToBytes(limit), GiB, GiB, nonpaged, null, null, null,
        ToBytes(processWorkingSet));

    private static ProcessSnapshot Process(ProcessIdentity identity, ulong privateCommit) => new(identity,
        "GrowingApp", 500 * MiB, privateCommit, 0, 0, 8, 40, "S-1-5-21-test", 1,
        true, true, false, DateTimeOffset.UtcNow - TimeSpan.FromHours(1), true);

    private static ulong ToBytes(double gibibytes) => (ulong)(gibibytes * GiB);
    private const ulong GiB = 1024UL * 1024 * 1024;
    private const ulong MiB = 1024UL * 1024;
}
