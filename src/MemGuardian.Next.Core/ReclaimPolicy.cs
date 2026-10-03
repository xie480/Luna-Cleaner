namespace MemGuardian.Next.Core;

/// <summary>按用户身份、活跃度和资源使用排序可回收进程，并给出所有拒绝原因。</summary>
public sealed class ReclaimCandidateSelector
{
    /// <summary>选择一轮最多两个可安全尝试的 Working Set 候选。</summary>
    public CandidateSelection Select(IReadOnlyList<ProcessSnapshot> processes, ControllerState state,
        ReclaimState reclaimState, GuardianOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(reclaimState);
        ArgumentNullException.ThrowIfNull(options);
        if (reclaimState.BackoffUntil is { } backoff && now < backoff)
            return new CandidateSelection(Array.Empty<ProcessSnapshot>(), Array.Empty<CandidateRejection>(), "Backoff active");
        if (reclaimState.StrategyScore < options.MinimumStrategyScoreForReclaim)
            return new CandidateSelection(Array.Empty<ProcessSnapshot>(), Array.Empty<CandidateRejection>(),
                "Adaptive strategy score is below the configured safety floor");
        if (reclaimState.GlobalCooldownUntil is { } cooldown && now < cooldown)
            return new CandidateSelection(Array.Empty<ProcessSnapshot>(), Array.Empty<CandidateRejection>(), "Global cooldown active");
        if (state is not (ControllerState.Pressure or ControllerState.Critical))
            return new CandidateSelection(Array.Empty<ProcessSnapshot>(), Array.Empty<CandidateRejection>(), "Pressure/Critical not confirmed");

        var roundLimit = Math.Clamp(options.MaximumProcessesPerRound, 0, 2);
        if (roundLimit == 0)
            return new CandidateSelection(Array.Empty<ProcessSnapshot>(), Array.Empty<CandidateRejection>(), "No processes permitted per round");
        var accepted = new List<ProcessSnapshot>();
        var rejected = new List<CandidateRejection>();
        foreach (var process in processes.OrderByDescending(value => value.WorkingSetBytes))
        {
            var reason = RejectionReason(process, reclaimState, options, now);
            if (reason is not null)
            {
                rejected.Add(new CandidateRejection(process.Identity, process.ProcessName, reason));
                continue;
            }

            accepted.Add(process);
            if (accepted.Count == roundLimit) break;
        }

        return new CandidateSelection(accepted, rejected, null);
    }

    private static string? RejectionReason(ProcessSnapshot process, ReclaimState state, GuardianOptions options,
        DateTimeOffset now)
    {
        if (process.Identity.ProcessId <= 0) return "Invalid or system PID";
        if (options.Denylist.Contains(process.ProcessName)) return "Denylist";
        if (!process.IsCurrentUser) return "Not owned by current user";
        if (!process.IsCurrentSession) return "Not in current user session";
        if (!process.CanTrim) return process.CollectionError ?? "Trim access unavailable";
        if (process.IsForeground) return "Foreground process";
        if (process.Identity.ProcessId == Environment.ProcessId) return "MemGuardian.Next process";
        if (process.LastUsedAt is null) return "Foreground-use history unknown";
        var idle = now - process.LastUsedAt.Value;
        if (idle < options.RecentUseProtection) return "Used within the protected interval";
        if (idle < options.MinimumIdleTime) return "Not idle long enough";
        if (process.WorkingSetBytes < options.MinimumCandidateWorkingSetBytes) return "Working Set below minimum";
        if (process.CpuPercent is null || process.CpuPercent > options.MaximumCandidateCpuPercent) return "CPU activity is high or unknown";
        if (process.IoBytesPerSecond is null || process.IoBytesPerSecond > options.MaximumCandidateIoBytesPerSecond)
            return "I/O activity is high or unknown";
        if (state.ProcessCooldowns.TryGetValue(process.Identity.ToKey(), out var cooldown) && now < cooldown)
            return "Process cooldown active";
        return null;
    }
}

/// <summary>将回收前后测量转换为 resident/Commit 结论和策略评分调整。</summary>
public sealed class ReclaimFeedbackEvaluator
{
    /// <summary>比较回收前后效果；缺失 paging 指标不会伪装为零流量。</summary>
    public ReclaimFeedback Evaluate(SystemMemoryMetrics before, SystemMemoryMetrics after,
        ulong workingSetBefore, ulong workingSetAfter, GuardianOptions options, bool targetsObserved = true)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(options);
        var reclaimed = targetsObserved && workingSetBefore > workingSetAfter ? workingSetBefore - workingSetAfter : 0;
        var availableDelta = ToSignedDelta(after.AvailablePhysicalBytes, before.AvailablePhysicalBytes);
        var commitDelta = ToSignedDelta(after.CommittedBytes, before.CommittedBytes);
        var readWorse = IsRateWorse(before.PageReadsPerSecond, after.PageReadsPerSecond, options);
        var inputWorse = IsRateWorse(before.PagesInputPerSecond, after.PagesInputPerSecond, options);
        var pagingObserved = before.PageReadsPerSecond is not null && after.PageReadsPerSecond is not null &&
                             before.PagesInputPerSecond is not null && after.PagesInputPerSecond is not null;
        var pagingWorse = readWorse || inputWorse;
        var negative = !targetsObserved || !pagingObserved ||
                       availableDelta < (long)options.MinimumAvailableIncreaseBytes || pagingWorse;
        var residentOnly = reclaimed >= options.MeaningfulWorkingSetDropBytes &&
                           Math.Abs((double)commitDelta) <= options.CommitUnchangedToleranceBytes;
        var scoreDelta = pagingWorse ? -0.5 : negative ? -0.25 : availableDelta >= (long)options.MinimumAvailableIncreaseBytes ? 0.1 : 0;
        var cooldown = pagingWorse ? options.BackoffMaximum : negative ? options.BackoffInitial : options.GlobalCooldown;
        var summary = residentOnly
            ? $"Resident pages reclaimed {FormatBytes(reclaimed)}; system Commit changed {FormatSignedBytes(commitDelta)}."
            : $"Working Set reclaimed {FormatBytes(reclaimed)}; Available RAM changed {FormatSignedBytes(availableDelta)}; Commit changed {FormatSignedBytes(commitDelta)}.";
        if (!targetsObserved) summary += " One or more target processes could not be observed after trim; outcome is unverified.";
        if (!pagingObserved) summary += " Paging counters were unavailable; outcome is unverified.";
        if (pagingWorse) summary += " Paging increased; entering Backoff.";
        else if (negative) summary += " Available RAM did not increase meaningfully; extending cooldown.";
        return new ReclaimFeedback(reclaimed, availableDelta, commitDelta, before.PageReadsPerSecond,
            after.PageReadsPerSecond, before.PagesInputPerSecond, after.PagesInputPerSecond, pagingObserved, pagingWorse,
            residentOnly, negative, cooldown, scoreDelta, targetsObserved, summary);
    }

    private static bool IsRateWorse(double? before, double? after, GuardianOptions options)
    {
        if (before is null || after is null) return false;
        return after.Value > Math.Max(before.Value * options.PagingRateIncreaseFactor,
            before.Value + options.PagingRateMinimumIncrease);
    }

    private static long ToSignedDelta(ulong after, ulong before) =>
        after >= before ? (long)Math.Min(after - before, (ulong)long.MaxValue) : -(long)Math.Min(before - after, (ulong)long.MaxValue);

    private static string FormatBytes(ulong bytes) => $"{bytes / 1024d / 1024d:F1} MiB";
    private static string FormatSignedBytes(long bytes) => $"{bytes / 1024d / 1024d:+0.0;-0.0;0.0} MiB";
}

/// <summary>Persists per-process cooldowns and adaptive score/backoff after a completed round.</summary>
public static class AdaptiveReclaimStateUpdater
{
    /// <summary>Returns new controller state based on measured round feedback.</summary>
    public static ReclaimState ApplyRound(ReclaimState current, ReclaimRoundResult result,
        GuardianOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        var cooldowns = new Dictionary<string, DateTimeOffset>(current.ProcessCooldowns, StringComparer.Ordinal);
        foreach (var attempt in result.Attempts)
            cooldowns[attempt.Identity.ToKey()] = now + options.ProcessCooldown;

        var feedback = result.Feedbacks.LastOrDefault();
        var globalCooldownUntil = now + options.GlobalCooldown;
        var negativeCount = current.ConsecutiveNegativeOutcomes;
        var strategyScore = current.StrategyScore;
        DateTimeOffset? backoffUntil = null;
        if (feedback is not null)
        {
            globalCooldownUntil = now + feedback.SuggestedCooldown;
            strategyScore = Math.Clamp(strategyScore + feedback.StrategyScoreDelta, 0, 1);
            if (feedback.OutcomeNegative)
            {
                negativeCount++;
                var seconds = Math.Min(options.BackoffMaximum.TotalSeconds,
                    options.BackoffInitial.TotalSeconds * Math.Pow(2, Math.Min(negativeCount, 10) - 1));
                var backoff = TimeSpan.FromSeconds(seconds);
                backoffUntil = now + backoff;
                if (backoffUntil > globalCooldownUntil) globalCooldownUntil = backoffUntil.Value;
            }
            else
            {
                negativeCount = 0;
            }
        }

        return current with
        {
            GlobalCooldownUntil = globalCooldownUntil,
            BackoffUntil = backoffUntil,
            ConsecutiveNegativeOutcomes = negativeCount,
            StrategyScore = strategyScore,
            ProcessCooldowns = cooldowns
        };
    }
}

/// <summary>将可注入的 trim 与采样器组合，dry-run 路径没有真实回收调用。</summary>
public sealed class AdaptiveReclaimCoordinator
{
    private readonly IWorkingSetReclaimer _reclaimer;
    private readonly ISystemSnapshotProvider _snapshotProvider;
    private readonly ReclaimFeedbackEvaluator _feedbackEvaluator;
    private readonly ReclaimCandidateSelector _candidateSelector;

    /// <summary>创建单轮回收协调器。</summary>
    public AdaptiveReclaimCoordinator(IWorkingSetReclaimer reclaimer, ISystemSnapshotProvider snapshotProvider,
        ReclaimFeedbackEvaluator feedbackEvaluator, ReclaimCandidateSelector candidateSelector)
    {
        _reclaimer = reclaimer;
        _snapshotProvider = snapshotProvider;
        _feedbackEvaluator = feedbackEvaluator;
        _candidateSelector = candidateSelector;
    }

    /// <summary>dry-run 仅返回候选；执行模式最多处理所选进程并等待反馈采样。</summary>
    public async Task<ReclaimRoundResult> ExecuteAsync(SystemSnapshot before, CandidateSelection selection,
        ControllerState state, ReclaimState reclaimState, bool dryRun, GuardianOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(selection);
        if (dryRun || selection.Candidates.Count == 0)
            return new ReclaimRoundResult(selection, Array.Empty<ReclaimAttempt>(), Array.Empty<ReclaimFeedback>(), dryRun);

        var targets = selection.Candidates.Take(Math.Clamp(options.MaximumProcessesPerRound, 0, 2)).ToArray();
        var attempts = new List<ReclaimAttempt>(targets.Length);
        var feedbacks = new List<ReclaimFeedback>(targets.Length);
        var currentSnapshot = before;
        var currentState = reclaimState;
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Re-sample process ownership, foreground status, CPU and I/O immediately before each native call.
            currentSnapshot = await _snapshotProvider.CaptureAsync(true, cancellationToken).ConfigureAwait(false);
            var currentTarget = currentSnapshot.Processes.FirstOrDefault(process => process.Identity == target.Identity);
            if (currentTarget is null)
            {
                attempts.Add(new ReclaimAttempt(target.Identity, target.ProcessName, false,
                    "Target process exited or could not be revalidated."));
                continue;
            }

            var revalidated = _candidateSelector.Select(new[] { currentTarget }, state, currentState, options,
                currentSnapshot.Memory.CapturedAt);
            if (revalidated.Candidates.Count == 0)
            {
                attempts.Add(new ReclaimAttempt(target.Identity, target.ProcessName, false,
                    revalidated.Rejections.FirstOrDefault()?.Reason ?? revalidated.BlockedBy));
                continue;
            }

            var result = _reclaimer.TryTrim(target.Identity);
            attempts.Add(new ReclaimAttempt(target.Identity, target.ProcessName, result.Succeeded, result.Error));
            if (!result.Succeeded) continue;

            // Once a native trim succeeds, finish its feedback sample even if the user cancels the monitor.
            await Task.Delay(options.FeedbackDelay, CancellationToken.None).ConfigureAwait(false);
            var after = await _snapshotProvider.CaptureAsync(true, CancellationToken.None).ConfigureAwait(false);
            var observedTarget = after.Processes.FirstOrDefault(process => process.Identity == target.Identity);
            var feedback = _feedbackEvaluator.Evaluate(currentSnapshot.Memory, after.Memory, currentTarget.WorkingSetBytes,
                observedTarget?.WorkingSetBytes ?? 0, options, observedTarget is not null);
            feedbacks.Add(feedback);
            var processCooldowns = new Dictionary<string, DateTimeOffset>(currentState.ProcessCooldowns, StringComparer.Ordinal)
            {
                [target.Identity.ToKey()] = after.Memory.CapturedAt + options.ProcessCooldown
            };
            currentState = currentState with { ProcessCooldowns = processCooldowns };
            currentSnapshot = after;
            if (feedback.OutcomeNegative) break;
        }

        return new ReclaimRoundResult(selection, attempts, feedbacks, false);
    }
}

/// <summary>文档化 Working Set trim 的窄接口，便于 dry-run 隔离和自动化测试。</summary>
public interface IWorkingSetReclaimer
{
    /// <summary>尝试回收目标实例 resident pages，并返回 native 错误信息。</summary>
    ReclaimerResult TryTrim(ProcessIdentity identity);
}

/// <summary>Native trim 操作的成功状态和可读错误。</summary>
public sealed record ReclaimerResult(bool Succeeded, string? Error);

/// <summary>只读系统快照接口，供反馈等待后重新测量。</summary>
public interface ISystemSnapshotProvider
{
    /// <summary>采集一次系统及进程快照。</summary>
    ValueTask<SystemSnapshot> CaptureAsync(bool refreshProcesses, CancellationToken cancellationToken = default);
}
