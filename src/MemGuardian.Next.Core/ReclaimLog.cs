namespace MemGuardian.Next.Core;

/// <summary>Whether a reclaim round was started by the user or the automatic policy.</summary>
public enum ReclaimTrigger
{
    Manual,
    Automatic
}

/// <summary>Result of a concrete reclaim attempt or an explicitly requested but safely skipped round.</summary>
public enum ReclaimLogStatus
{
    Succeeded,
    Failed,
    Skipped
}

/// <summary>Bounded audit entry that distinguishes resident pages from system commit.</summary>
public sealed record ReclaimLogEntry(
    DateTimeOffset CapturedAt,
    ReclaimTrigger Trigger,
    ReclaimLogStatus Status,
    ProcessIdentity? TargetIdentity,
    string? TargetProcessName,
    ulong WorkingSetReclaimedBytes,
    long AvailableRamDeltaBytes,
    long CommitDeltaBytes,
    bool ResidentPagesOnly,
    double? PageReadsBefore,
    double? PageReadsAfter,
    double? PagesInputBefore,
    double? PagesInputAfter,
    bool PagingWorsened,
    string Summary);

/// <summary>Maps measured rounds into user-facing audit entries without equating Working Set with Commit.</summary>
public static class ReclaimLogBuilder
{
    public static IReadOnlyList<ReclaimLogEntry> FromRound(ReclaimRoundResult round, ReclaimTrigger trigger,
        DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(round);
        var feedbackByTarget = round.Feedbacks.Where(item => item.TargetIdentity is not null)
            .ToDictionary(item => item.TargetIdentity!.Value);
        return round.Attempts.Select(attempt =>
        {
            feedbackByTarget.TryGetValue(attempt.Identity, out var feedback);
            var status = !attempt.NativeCallAttempted
                ? ReclaimLogStatus.Skipped
                : attempt.Succeeded ? ReclaimLogStatus.Succeeded : ReclaimLogStatus.Failed;
            var summary = feedback is not null
                ? FormatFeedbackSummary(feedback)
                : status switch
                {
                    ReclaimLogStatus.Skipped => TranslateReason(attempt.Error) ?? "进程在执行前未通过重新验证，已安全跳过。",
                    ReclaimLogStatus.Failed => $"EmptyWorkingSet 调用失败：{attempt.Error ?? "没有可用的错误详情。"}",
                    _ => "EmptyWorkingSet 调用成功；回收后的系统反馈不可用。"
                };
            return new ReclaimLogEntry(feedback?.CapturedAt ?? capturedAt, trigger, status,
                attempt.Identity, attempt.ProcessName, feedback?.WorkingSetReclaimedBytes ?? 0,
                feedback?.AvailableRamDeltaBytes ?? 0, feedback?.CommitDeltaBytes ?? 0,
                feedback?.ResidentPagesOnly ?? false, feedback?.PageReadsBefore, feedback?.PageReadsAfter,
                feedback?.PagesInputBefore, feedback?.PagesInputAfter, feedback?.PagingWorsened ?? false, summary);
        }).ToArray();
    }

    public static ReclaimLogEntry Skipped(ReclaimTrigger trigger, DateTimeOffset capturedAt, string reason) =>
        new(capturedAt, trigger, ReclaimLogStatus.Skipped, null, null, 0, 0, 0, false,
            null, null, null, null, false, reason);

    public static ReclaimLogEntry Failed(ReclaimTrigger trigger, DateTimeOffset capturedAt, string reason) =>
        new(capturedAt, trigger, ReclaimLogStatus.Failed, null, null, 0, 0, 0, false,
            null, null, null, null, false, reason);

    private static string FormatFeedbackSummary(ReclaimFeedback feedback)
    {
        var result = $"驻留页回收 {FormatBytes(feedback.WorkingSetReclaimedBytes)}；Available RAM {FormatDelta(feedback.AvailableRamDeltaBytes)}；系统 Commit {FormatDelta(feedback.CommitDeltaBytes)}。";
        if (feedback.ResidentPagesOnly)
            result += " 系统 Commit 基本未变；这是 resident pages 回收，不代表释放了同等大小的 Commit。";
        result += $" Page Reads/sec {FormatRate(feedback.PageReadsBefore)} → {FormatRate(feedback.PageReadsAfter)}；Pages Input/sec {FormatRate(feedback.PagesInputBefore)} → {FormatRate(feedback.PagesInputAfter)}。";
        if (!feedback.TargetsObserved) result += " 反馈采样无法观察到目标进程，实际 Working Set 效果无法确认。";
        if (!feedback.PagingObserved) result += " paging 计数器不可用，无法确认页面读取反馈。";
        if (feedback.PagingWorsened) result += " 页面读取恶化，控制器已进入 Backoff。";
        else if (feedback.OutcomeNegative) result += " 可用内存提升不足或反馈不完整，控制器已延长冷却。";
        return result;
    }

    private static string? TranslateReason(string? reason) => reason switch
    {
        "Target process exited or could not be revalidated." => "目标进程已退出或无法重新验证，已安全跳过。",
        "Foreground process" => "目标已成为前台程序，已安全跳过。",
        "Used within the protected interval" => "目标最近使用过，仍处于保护期，已安全跳过。",
        "Process cooldown active" => "目标进程仍在冷却期，已安全跳过。",
        "Denylist" => "目标位于 denylist，已安全跳过。",
        _ => reason
    };

    private static string FormatDelta(long bytes) => bytes switch
    {
        > 0 => $"+{FormatBytes((ulong)bytes)}",
        < 0 => $"−{FormatBytes(bytes == long.MinValue ? (ulong)long.MaxValue + 1 : (ulong)Math.Abs(bytes))}",
        _ => "0 B"
    };

    private static string FormatBytes(ulong bytes) => bytes == 0 ? "0 B" : bytes >= 1024UL * 1024 * 1024
        ? $"{bytes / 1024d / 1024 / 1024:F2} GiB"
        : bytes >= 1024UL * 1024 ? $"{bytes / 1024d / 1024:F1} MiB"
        : bytes >= 1024 ? $"{bytes / 1024d:F0} KiB" : $"{bytes} B";

    private static string FormatRate(double? value) => value is { } rate ? $"{rate:F1}" : "不可用";
}
