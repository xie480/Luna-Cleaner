using MemGuardian.Next.Core;
using Xunit;

namespace MemGuardian.Next.Tests;

/// <summary>Exercises candidate protection, state cooldown, dry-run isolation and adaptive paging backoff.</summary>
public sealed class PolicyTests
{
    /// <summary>前台且最近用过的进程不能成为 Working Set 候选。</summary>
    [Fact]
    public void ForegroundAndRecentlyUsedProcessesAreProtected()
    {
        var now = DateTimeOffset.UtcNow;
        var process = Candidate(2200, now, foreground: true, lastUsed: now - TimeSpan.FromSeconds(30));
        var result = new ReclaimCandidateSelector().Select(new[] { process }, ControllerState.Pressure,
            new ReclaimState(), new GuardianOptions(), now);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Rejections, rejected => rejected.Reason == "Foreground process");

        var recentlyUsed = Candidate(2201, now, lastUsed: now - TimeSpan.FromSeconds(30));
        var recentResult = new ReclaimCandidateSelector().Select(new[] { recentlyUsed }, ControllerState.Pressure,
            new ReclaimState(), new GuardianOptions(), now);
        Assert.Empty(recentResult.Candidates);
        Assert.Contains(recentResult.Rejections, rejected => rejected.Reason == "Used within the protected interval");
    }

    /// <summary>全局 cooldown 与进程 cooldown 都会阻止真实候选。</summary>
    [Fact]
    public void GlobalAndPerProcessCooldownAreEnforced()
    {
        var now = DateTimeOffset.UtcNow;
        var process = Candidate(2300, now);
        var selector = new ReclaimCandidateSelector();
        var global = selector.Select(new[] { process }, ControllerState.Pressure,
            new ReclaimState { GlobalCooldownUntil = now + TimeSpan.FromMinutes(1) }, new GuardianOptions(), now);
        var processState = new ReclaimState
        {
            ProcessCooldowns = new Dictionary<string, DateTimeOffset>
            {
                [process.Identity.ToKey()] = now + TimeSpan.FromMinutes(1)
            }
        };
        var local = selector.Select(new[] { process }, ControllerState.Pressure, processState, new GuardianOptions(), now);
        Assert.Equal("Global cooldown active", global.BlockedBy);
        Assert.Empty(local.Candidates);
        Assert.Contains(local.Rejections, rejected => rejected.Reason == "Process cooldown active");
    }

    /// <summary>无论配置如何，单轮候选上限都不会超过两个进程。</summary>
    [Fact]
    public void ReclaimRoundNeverSelectsMoreThanTwoProcesses()
    {
        var now = DateTimeOffset.UtcNow;
        var processes = new[] { Candidate(2310, now), Candidate(2311, now), Candidate(2312, now) };
        var result = new ReclaimCandidateSelector().Select(processes, ControllerState.Pressure,
            new ReclaimState(), new GuardianOptions { MaximumProcessesPerRound = 99 }, now);

        Assert.Equal(2, result.Candidates.Count);
    }

    /// <summary>Dry-run 使用相同候选集，但不会触发 reclaimer 或 feedback sampler。</summary>
    [Fact]
    public async Task DryRunNeverCallsReclaimerOrFeedbackSampler()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(now, new[] { Candidate(2400, now) });
        var selection = new CandidateSelection(snapshot.Processes, Array.Empty<CandidateRejection>(), null);
        var reclaimer = new FakeReclaimer();
        var sampler = new FakeSnapshotProvider();
        var coordinator = new AdaptiveReclaimCoordinator(reclaimer, sampler, new ReclaimFeedbackEvaluator(),
            new ReclaimCandidateSelector());
        var result = await coordinator.ExecuteAsync(snapshot, selection, ControllerState.Pressure,
            new ReclaimState(), dryRun: true, new GuardianOptions());
        Assert.True(result.DryRun);
        Assert.Empty(result.Attempts);
        Assert.Empty(result.Feedbacks);
        Assert.Equal(0, reclaimer.Calls);
        Assert.Equal(0, sampler.Calls);
    }

    /// <summary>全局 cooldown 未结束时控制器暴露 Cooldown 状态。</summary>
    [Fact]
    public void ControllerReportsCooldownUntilItsDeadline()
    {
        var now = DateTimeOffset.UtcNow;
        var machine = new MemoryStateMachine();
        var state = machine.Observe(PressureLevel.Critical, now, new GuardianOptions(),
            new ReclaimState { GlobalCooldownUntil = now + TimeSpan.FromMinutes(1) });
        Assert.Equal(ControllerState.Cooldown, state);
    }

    /// <summary>状态机要求压力持续确认，恢复还需更长时间且带迟滞。</summary>
    [Fact]
    public void PressureRequiresPersistenceAndRecoveryUsesLongerHysteresis()
    {
        var now = DateTimeOffset.UtcNow;
        var options = new GuardianOptions();
        var machine = new MemoryStateMachine();
        Assert.Equal(ControllerState.Normal, machine.Observe(PressureLevel.Pressure, now, options, new ReclaimState()));
        Assert.Equal(ControllerState.Pressure, machine.Observe(PressureLevel.Pressure,
            now + options.PressureConfirmation, options, new ReclaimState()));
        var recoveryStarted = now + options.PressureConfirmation;
        Assert.Equal(ControllerState.Pressure, machine.Observe(PressureLevel.Normal,
            recoveryStarted, options, new ReclaimState()));
        Assert.Equal(ControllerState.Pressure, machine.Observe(PressureLevel.Normal,
            recoveryStarted + options.RecoveryConfirmation - TimeSpan.FromSeconds(1), options, new ReclaimState()));
        Assert.Equal(ControllerState.Normal, machine.Observe(PressureLevel.Normal,
            recoveryStarted + options.RecoveryConfirmation, options, new ReclaimState()));
    }

    /// <summary>一次回收后 paging 变差时立即停止本轮第二个候选并产出 Backoff 信号。</summary>
    [Fact]
    public async Task PagingWorseningStopsTheNextTrimAndRequestsBackoff()
    {
        var now = DateTimeOffset.UtcNow;
        var first = Candidate(2500, now);
        var second = Candidate(2501, now);
        var before = Snapshot(now, new[] { first, second }, pageReads: 5, pagesInput: 50);
        var afterFirst = Snapshot(now + TimeSpan.FromSeconds(5), new[]
        {
            first with { WorkingSetBytes = 100 * MiB }, second
        }, available: 10, commit: 20, pageReads: 30, pagesInput: 100);
        var beforeTrim = Snapshot(now + TimeSpan.FromSeconds(1), new[] { first, second },
            available: 4, commit: 24, pageReads: 5, pagesInput: 50);
        var reclaimer = new FakeReclaimer();
        var sampler = new FakeSnapshotProvider(beforeTrim, afterFirst);
        var selection = new ReclaimCandidateSelector().Select(before.Processes, ControllerState.Pressure,
            new ReclaimState(), new GuardianOptions(), now);
        var coordinator = new AdaptiveReclaimCoordinator(reclaimer, sampler, new ReclaimFeedbackEvaluator(),
            new ReclaimCandidateSelector());
        var result = await coordinator.ExecuteAsync(before, selection, ControllerState.Pressure,
            new ReclaimState(), dryRun: false, new GuardianOptions { FeedbackDelay = TimeSpan.Zero });
        Assert.Single(result.Attempts);
        Assert.Single(result.Feedbacks);
        Assert.Equal(1, reclaimer.Calls);
        Assert.True(result.Feedbacks[0].PagingObserved);
        Assert.True(result.Feedbacks[0].PagingWorsened);
        Assert.True(result.Feedbacks[0].OutcomeNegative);
        Assert.Equal(TimeSpan.FromHours(1), result.Feedbacks[0].SuggestedCooldown);
        var backedOff = AdaptiveReclaimStateUpdater.ApplyRound(new ReclaimState(), result, new GuardianOptions(), now);
        Assert.True(backedOff.StrategyScore < 1);
        Assert.Equal(1, backedOff.ConsecutiveNegativeOutcomes);
        Assert.Equal(now + TimeSpan.FromMinutes(15), backedOff.BackoffUntil);
        Assert.Equal(ControllerState.Backoff, new MemoryStateMachine().Observe(PressureLevel.Critical, now,
            new GuardianOptions(), backedOff));
    }

    /// <summary>前台状态在 dry-run 后变化时，执行前的新快照会拦截 trim。</summary>
    [Fact]
    public async Task RevalidatesForegroundStatusImmediatelyBeforeTrim()
    {
        var now = DateTimeOffset.UtcNow;
        var candidate = Candidate(2510, now);
        var before = Snapshot(now, new[] { candidate });
        var selected = new ReclaimCandidateSelector().Select(before.Processes, ControllerState.Pressure,
            new ReclaimState(), new GuardianOptions(), now);
        var becameForeground = Snapshot(now + TimeSpan.FromSeconds(1),
            new[] { candidate with { IsForeground = true } });
        var reclaimer = new FakeReclaimer();
        var sampler = new FakeSnapshotProvider(becameForeground);
        var coordinator = new AdaptiveReclaimCoordinator(reclaimer, sampler, new ReclaimFeedbackEvaluator(),
            new ReclaimCandidateSelector());

        var result = await coordinator.ExecuteAsync(before, selected, ControllerState.Pressure,
            new ReclaimState(), dryRun: false, new GuardianOptions());

        Assert.Single(result.Attempts);
        Assert.False(result.Attempts[0].Succeeded);
        Assert.Contains("Foreground", result.Attempts[0].Error);
        Assert.Equal(0, reclaimer.Calls);
        Assert.Empty(result.Feedbacks);
    }

    /// <summary>单进程 Working Set 下降但 Commit 持平时反馈只称为 resident pages reclaimed。</summary>
    [Fact]
    public void StableCommitIsReportedAsResidentPagesOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var before = Snapshot(now, Array.Empty<ProcessSnapshot>(), pageReads: 1, pagesInput: 2).Memory;
        var after = before with
        {
            CapturedAt = now + TimeSpan.FromSeconds(5),
            AvailablePhysicalBytes = before.AvailablePhysicalBytes + GiB,
            PageReadsPerSecond = 1,
            PagesInputPerSecond = 2
        };
        var result = new ReclaimFeedbackEvaluator().Evaluate(before, after, 800 * MiB, 100 * MiB,
            new GuardianOptions());
        Assert.True(result.ResidentPagesOnly);
        Assert.Contains("Resident pages", result.Summary);
        Assert.Equal(0, result.CommitDeltaBytes);
        Assert.Equal(before.AvailablePhysicalBytes, result.AvailableRamBeforeBytes);
        Assert.Equal(after.AvailablePhysicalBytes, result.AvailableRamAfterBytes);
        Assert.Equal(before.CommittedBytes, result.SystemCommitBeforeBytes);
        Assert.Equal(after.CommittedBytes, result.SystemCommitAfterBytes);
        Assert.Equal(800UL * MiB, result.TargetWorkingSetBeforeBytes);
        Assert.Equal(100UL * MiB, result.TargetWorkingSetAfterBytes);
        Assert.Contains("Page Reads/sec 1.0 -> 1.0", result.Summary);
        Assert.Contains("Pages Input/sec 2.0 -> 2.0", result.Summary);
    }

    private static ProcessSnapshot Candidate(int pid, DateTimeOffset now, bool foreground = false,
        DateTimeOffset? lastUsed = null) => new(new ProcessIdentity(pid, pid * 10L), "IdleApp",
        600 * MiB, 700 * MiB, 0, 0, 8, 50, "S-1-5-21-current", 1,
        true, true, foreground, lastUsed ?? now - TimeSpan.FromMinutes(20), true);

    private static SystemSnapshot Snapshot(DateTimeOffset now, IReadOnlyList<ProcessSnapshot> processes,
        double available = 4, double commit = 24, double pageReads = 1, double pagesInput = 2) =>
        new(new SystemMemoryMetrics(now, 32 * GiB, (ulong)(available * GiB), (ulong)(commit * GiB),
            64 * GiB, GiB, GiB, 128 * MiB, pageReads, pagesInput, 20,
            processes.Aggregate(0UL, (total, process) => total + process.WorkingSetBytes)), processes);

    private sealed class FakeReclaimer : IWorkingSetReclaimer
    {
        public int Calls { get; private set; }
        public ReclaimerResult TryTrim(ProcessIdentity identity)
        {
            Calls++;
            return new ReclaimerResult(true, null);
        }
    }

    private sealed class FakeSnapshotProvider : ISystemSnapshotProvider
    {
        private readonly Queue<SystemSnapshot> _samples;
        public int Calls { get; private set; }
        public FakeSnapshotProvider(params SystemSnapshot[] samples) => _samples = new Queue<SystemSnapshot>(samples);
        public ValueTask<SystemSnapshot> CaptureAsync(bool refreshProcesses, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (_samples.Count == 0) throw new InvalidOperationException("Unexpected feedback sample.");
            return ValueTask.FromResult(_samples.Dequeue());
        }
    }

    private const ulong GiB = 1024UL * 1024 * 1024;
    private const ulong MiB = 1024UL * 1024;
}
