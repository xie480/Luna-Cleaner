using System.Globalization;
using MemGuardian.Next.Core;

namespace MemGuardian.Next.Cli;

/// <summary>Renders stable text output for diagnosis, process ranking, dry-run and feedback.</summary>
internal static class ConsolePresenter
{
    internal static void PrintHelp()
    {
        Console.WriteLine("MemGuardian.Next - Windows 内存压力诊断与自适应回收");
        Console.WriteLine("  memguardian status");
        Console.WriteLine("  memguardian top --by working-set|commit");
        Console.WriteLine("  memguardian diagnose [--duration 1..3600]");
        Console.WriteLine("  memguardian run [--dry-run]");
        Console.WriteLine("  memguardian once");
        Console.WriteLine("首次建议使用 run --dry-run 观察状态、候选和排除原因。");
    }

    internal static void PrintStatus(SystemSnapshot snapshot, DiagnosisResult diagnosis, ControllerState state,
        IReadOnlyList<HistoryEntry> history)
    {
        var memory = snapshot.Memory;
        Console.WriteLine($"状态：{state}（诊断级别：{diagnosis.Level}）");
        Console.WriteLine($"Physical RAM：{Bytes(memory.TotalPhysicalBytes)}    Available RAM：{Bytes(memory.AvailablePhysicalBytes)} ({memory.AvailablePhysicalRatio:P1})");
        Console.WriteLine($"进程 Working Set 合计：{Bytes(memory.AggregateProcessWorkingSetBytes)}（共享页可能重复计数）");
        Console.WriteLine($"System Commit：{Bytes(memory.CommittedBytes)} / {Bytes(memory.CommitLimitBytes)} ({Ratio(memory.CommitRatio)})");
        Console.WriteLine($"System Cache：{Bytes(memory.SystemCacheBytes)}    Paged Pool：{Bytes(memory.PagedPoolBytes)}    Nonpaged Pool：{Bytes(memory.NonpagedPoolBytes)}");
        Console.WriteLine($"Pagefile usage：{Percent(memory.PagefileUsagePercent)}    Page Reads/sec：{Rate(memory.PageReadsPerSecond)}    Pages Input/sec：{Rate(memory.PagesInputPerSecond)}");
        Console.WriteLine($"进程数：{snapshot.Processes.Count}    趋势样本：{history.Count}    采样时间：{memory.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        PrintDiagnosis(diagnosis);
    }

    internal static void PrintTop(SystemSnapshot snapshot, TopSort sort)
    {
        var ordered = sort == TopSort.WorkingSet
            ? snapshot.Processes.OrderByDescending(process => process.WorkingSetBytes)
            : snapshot.Processes.OrderByDescending(process => process.PrivateCommitBytes);
        Console.WriteLine($"Top 20 by {(sort == TopSort.WorkingSet ? "Working Set" : "Private Commit")}");
        Console.WriteLine("PID     Process                   Working Set     Private Commit  CPU     I/O/s      Threads Handles Sess Owner     Foreground Last use");
        foreach (var process in ordered.Take(20))
        {
            var user = process.UserSid is null ? "unknown" : process.IsCurrentUser ? "current" : process.UserSid;
            var used = process.LastUsedAt is null ? "unknown" : Age(DateTimeOffset.UtcNow - process.LastUsedAt.Value);
            var workingSet = process.CollectionError is null ? Bytes(process.WorkingSetBytes) : "unavailable";
            var commit = process.CollectionError is null ? Bytes(process.PrivateCommitBytes) : "unavailable";
            Console.WriteLine($"{process.Identity.ProcessId,-7} {Truncate(process.ProcessName, 24),-24} {workingSet,14} {commit,16} {Percent(process.CpuPercent),7} {Rate(process.IoBytesPerSecond),10} {process.ThreadCount,7} {process.HandleCount,7} {process.SessionId?.ToString(CultureInfo.InvariantCulture) ?? "?",4} {Truncate(user, 16),-16} {(process.IsForeground ? "YES" : "no"),10} {used}");
        }
    }

    internal static void PrintDiagnosis(DiagnosisResult diagnosis)
    {
        Console.WriteLine("诊断：");
        if (diagnosis.Flags.Count == 0)
        {
            Console.WriteLine("  未发现当前规则定义的压力信号；这不等于设备在所有负载下都无压力。");
            return;
        }
        foreach (var flag in diagnosis.Flags.OrderBy(flag => flag)) Console.WriteLine($"  {flag}");
        foreach (var reason in diagnosis.Evidence) Console.WriteLine($"  证据：{SafeText(reason)}");
    }

    internal static void PrintCandidates(CandidateSelection selection, bool dryRun)
    {
        Console.WriteLine(dryRun ? "Dry-run：只完成候选选择，没有调用 EmptyWorkingSet。" : "已完成本轮候选选择。");
        if (selection.BlockedBy is not null) Console.WriteLine($"  暂不可回收：{selection.BlockedBy}");
        foreach (var candidate in selection.Candidates)
            Console.WriteLine($"  候选 PID {candidate.Identity.ProcessId} {SafeText(candidate.ProcessName)}，Working Set {Bytes(candidate.WorkingSetBytes)}，Private Commit {Bytes(candidate.PrivateCommitBytes)}");
        foreach (var rejected in selection.Rejections)
            Console.WriteLine($"  跳过 PID {rejected.Identity.ProcessId} {SafeText(rejected.ProcessName)}：{SafeText(rejected.Reason)}");
        if (selection.Candidates.Count == 0 && selection.BlockedBy is null) Console.WriteLine("  没有满足全部安全条件的候选进程。");
    }

    internal static void PrintRound(ReclaimRoundResult result)
    {
        foreach (var attempt in result.Attempts)
        {
            var outcome = attempt.Succeeded ? "Working Set trim 已调用" : $"跳过/失败：{SafeText(attempt.Error ?? "原因未知")}";
            Console.WriteLine($"PID {attempt.Identity.ProcessId} {SafeText(attempt.ProcessName)}：{outcome}");
        }
        foreach (var feedback in result.Feedbacks)
        {
            Console.WriteLine($"  {feedback.Summary}");
            Console.WriteLine($"  Available RAM 变化 {SignedBytes(feedback.AvailableRamDeltaBytes)}；System Commit 变化 {SignedBytes(feedback.CommitDeltaBytes)}；Paging {(feedback.PagingObserved ? (feedback.PagingWorsened ? "恶化" : "未见阈值恶化") : "无法验证")}");
        }
    }

    internal static string Bytes(ulong value) => $"{value / 1024d / 1024d / 1024d:F2} GiB";
    internal static string SignedBytes(long value) => $"{value / 1024d / 1024d:+0.0;-0.0;0.0} MiB";
    internal static string Rate(double? value) => value is null ? "n/a" : $"{value.Value.ToString("N1", CultureInfo.InvariantCulture)}/s";
    internal static string Percent(double? value) => value is null ? "n/a" : value.Value.ToString("P1", CultureInfo.InvariantCulture);
    internal static string Ratio(double? value) => Percent(value);
    internal static string Age(TimeSpan age) => age < TimeSpan.Zero ? "now" : age.TotalMinutes < 1 ? $"{Math.Max(0, age.TotalSeconds):F0}s ago" : $"{age.TotalMinutes:F0}m ago";

    private static string Truncate(string value, int length)
    {
        var safe = SafeText(value);
        return safe.Length <= length ? safe : safe[..(length - 1)] + "…";
    }

    private static string SafeText(string value) => string.Concat(value.Select(character =>
        char.IsControl(character) || char.GetUnicodeCategory(character) is UnicodeCategory.Format or
            UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' ' : character));
}
