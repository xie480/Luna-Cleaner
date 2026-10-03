namespace MemGuardian.Next.Core;

/// <summary>依据多类内存证据形成可并存诊断标记和压力级别。</summary>
public sealed class DiagnosisEngine
{
    /// <summary>分析物理内存、Commit、paging、内核池和有界趋势。</summary>
    public DiagnosisResult Analyze(SystemSnapshot snapshot, IReadOnlyList<HistoryEntry> history, GuardianOptions options,
        PressureLevel? previousLevel = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(options);

        var memory = snapshot.Memory;
        var flags = new HashSet<DiagnosticFlag>();
        var evidence = new List<string>();
        var availableRatio = memory.AvailablePhysicalRatio;
        var commitRatio = memory.CommitRatio ?? 0;
        var poolRatio = memory.TotalPhysicalBytes == 0 ? 0 :
            ((double)memory.PagedPoolBytes + memory.NonpagedPoolBytes) / memory.TotalPhysicalBytes;

        if (availableRatio <= options.AvailableWatchRatio)
        {
            flags.Add(DiagnosticFlag.LowAvailableRam);
            evidence.Add($"Available RAM {availableRatio:P1} of physical memory");
        }

        if (availableRatio <= options.AvailablePressureRatio && commitRatio < options.CommitPressureRatio &&
            memory.AggregateProcessWorkingSetBytes >= memory.TotalPhysicalBytes * 0.55)
        {
            flags.Add(DiagnosticFlag.WorkingSetPressure);
            evidence.Add("Low Available RAM with high aggregate process Working Set and lower system Commit use");
        }

        if (memory.CommitRatio is not null && commitRatio >= options.CommitWatchRatio)
        {
            flags.Add(DiagnosticFlag.CommitPressure);
            evidence.Add($"System Commit is {commitRatio:P1} of its limit");
        }

        var pagingElevated = IsPagingSustained(history, options);
        if (pagingElevated)
        {
            flags.Add(DiagnosticFlag.PagingPressure);
            evidence.Add("Page Reads/sec or Pages Input/sec stayed above its configured threshold");
        }

        var processLeaks = FindProcessLeaks(history, options);
        if (processLeaks.Count > 0)
        {
            flags.Add(DiagnosticFlag.PossibleProcessLeak);
            foreach (var identity in processLeaks)
            {
                var name = snapshot.Processes.FirstOrDefault(process => process.Identity == identity)?.ProcessName;
                evidence.Add($"PID {identity.ProcessId}{(name is null ? string.Empty : $" ({name})")} Private Commit grew persistently");
            }
        }

        var driverLeak = HasDriverLeak(history, options);
        if (driverLeak)
        {
            flags.Add(DiagnosticFlag.PossibleDriverLeak);
            evidence.Add("Nonpaged Pool grew persistently across the configured leak window");
        }

        if (poolRatio >= options.KernelPoolWatchRatio || driverLeak)
        {
            flags.Add(DiagnosticFlag.KernelPoolPressure);
            evidence.Add($"Paged and nonpaged kernel pool total {poolRatio:P1} of physical memory");
        }

        if (memory.PagefileUsagePercent is >= 80)
        {
            flags.Add(DiagnosticFlag.PagefileUsageHigh);
            evidence.Add($"Pagefile usage is {memory.PagefileUsagePercent:P1}");
        }

        var criticalPaging = pagingElevated && memory.PageReadsPerSecond >= options.PageReadsCriticalPerSecond &&
            availableRatio <= options.AvailableWatchRatio;
        var level = availableRatio <= options.AvailableCriticalRatio || commitRatio >= options.CommitCriticalRatio || criticalPaging
            ? PressureLevel.Critical
            : availableRatio <= options.AvailablePressureRatio || commitRatio >= options.CommitPressureRatio ||
              (pagingElevated && availableRatio <= options.AvailableWatchRatio)
                ? PressureLevel.Pressure
                : flags.Count > 0 ? PressureLevel.Watch : PressureLevel.Normal;

        if (previousLevel == PressureLevel.Critical && (int)level < (int)PressureLevel.Critical &&
            (availableRatio <= options.AvailablePressureRatio || commitRatio >= options.CommitPressureRatio || criticalPaging))
            level = PressureLevel.Critical;
        else if (previousLevel == PressureLevel.Pressure && (int)level < (int)PressureLevel.Pressure &&
                 (availableRatio < options.AvailableRecoveryRatio || commitRatio > options.CommitRecoveryRatio || pagingElevated))
            level = PressureLevel.Pressure;
        else if (previousLevel == PressureLevel.Watch && (int)level < (int)PressureLevel.Watch &&
                 (availableRatio < options.AvailableRecoveryRatio || commitRatio > options.CommitRecoveryRatio || pagingElevated))
            level = PressureLevel.Watch;

        return new DiagnosisResult(level, flags, evidence);
    }

    private static bool IsPagingSustained(IReadOnlyList<HistoryEntry> history, GuardianOptions options)
    {
        if (history.Count < 3) return false;
        var latest = history[^1].CapturedAt;
        var recent = history.Where(entry => entry.CapturedAt >= latest - TimeSpan.FromSeconds(30))
            .Where(entry => entry.PageReadsPerSecond is not null || entry.PagesInputPerSecond is not null)
            .ToArray();
        if (recent.Length < 3 || recent[^1].CapturedAt - recent[0].CapturedAt < TimeSpan.FromSeconds(10)) return false;
        return recent.Count(entry => (entry.PageReadsPerSecond ?? 0) >= options.PageReadsAlertPerSecond ||
                                     (entry.PagesInputPerSecond ?? 0) >= options.PagesInputAlertPerSecond) >= 3;
    }

    private static IReadOnlyList<ProcessIdentity> FindProcessLeaks(IReadOnlyList<HistoryEntry> history, GuardianOptions options)
    {
        if (history.Count < 5) return Array.Empty<ProcessIdentity>();
        var latest = history[^1].CapturedAt;
        var window = history.Where(entry => entry.CapturedAt >= latest - options.LeakWindow && entry.ProcessCommits.Count > 0).ToArray();
        if (window.Length < 5 || window[^1].CapturedAt - window[0].CapturedAt < options.LeakWindow)
            return Array.Empty<ProcessIdentity>();
        var first = window[0].ProcessCommits.ToDictionary(value => value.Identity, value => value.PrivateCommitBytes);
        var candidates = window[^1].ProcessCommits.Where(value => first.ContainsKey(value.Identity) &&
            value.PrivateCommitBytes >= first[value.Identity] &&
            value.PrivateCommitBytes - first[value.Identity] >= options.ProcessLeakGrowthBytes);
        var leaks = new List<ProcessIdentity>();
        foreach (var candidate in candidates)
        {
            var values = window.Select(entry => entry.ProcessCommits.FirstOrDefault(value => value.Identity == candidate.Identity))
                .Where(value => value.Identity == candidate.Identity).Select(value => value.PrivateCommitBytes).ToArray();
            if (values.Length >= 5 && CountIncreases(values) >= (values.Length - 1) * 0.7) leaks.Add(candidate.Identity);
        }

        return leaks;
    }

    private static bool HasDriverLeak(IReadOnlyList<HistoryEntry> history, GuardianOptions options)
    {
        if (history.Count < 5) return false;
        var latest = history[^1].CapturedAt;
        var window = history.Where(entry => entry.CapturedAt >= latest - options.LeakWindow).ToArray();
        if (window.Length < 5 || window[^1].CapturedAt - window[0].CapturedAt < options.LeakWindow) return false;
        var values = window.Select(entry => entry.NonpagedPoolBytes).ToArray();
        return values[^1] > values[0] && values[^1] - values[0] >= options.DriverLeakGrowthBytes &&
               CountIncreases(values) >= (values.Length - 1) * 0.7;
    }

    private static int CountIncreases(IReadOnlyList<ulong> values)
    {
        var count = 0;
        for (var index = 1; index < values.Count; index++)
            if (values[index] > values[index - 1]) count++;
        return count;
    }
}
