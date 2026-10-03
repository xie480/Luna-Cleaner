namespace MemGuardian.Next.Core;

/// <summary>稳定标识进程实例，避免 PID 重用污染趋势和 cooldown。</summary>
public readonly record struct ProcessIdentity(int ProcessId, long CreationTimeFileTime)
{
    /// <summary>返回可持久化且不依赖文化设置的身份键。</summary>
    public string ToKey() => $"{ProcessId}:{CreationTimeFileTime}";
}

/// <summary>进程快照中的用户可见名称；SID 只用于本机身份比较，不写入控制台。</summary>
public sealed record ProcessSnapshot(
    ProcessIdentity Identity,
    string ProcessName,
    ulong WorkingSetBytes,
    ulong PrivateCommitBytes,
    double? CpuPercent,
    double? IoBytesPerSecond,
    int ThreadCount,
    int HandleCount,
    string? UserSid,
    int? SessionId,
    bool IsCurrentUser,
    bool IsCurrentSession,
    bool IsForeground,
    DateTimeOffset? LastUsedAt,
    bool CanTrim,
    string? CollectionError = null);

/// <summary>单次系统内存采样。缺失的 PDH 指标使用 null 表示，不以零代替。</summary>
public sealed record SystemMemoryMetrics(
    DateTimeOffset CapturedAt,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    ulong CommittedBytes,
    ulong CommitLimitBytes,
    ulong SystemCacheBytes,
    ulong PagedPoolBytes,
    ulong NonpagedPoolBytes,
    double? PageReadsPerSecond,
    double? PagesInputPerSecond,
    double? PagefileUsagePercent,
    ulong AggregateProcessWorkingSetBytes)
{
    /// <summary>返回系统 Commit 使用率；CommitLimit 不可用时返回 null。</summary>
    public double? CommitRatio => CommitLimitBytes == 0 ? null : (double)CommittedBytes / CommitLimitBytes;

    /// <summary>返回当前可用物理内存占比。</summary>
    public double AvailablePhysicalRatio => TotalPhysicalBytes == 0 ? 0 : (double)AvailablePhysicalBytes / TotalPhysicalBytes;
}

/// <summary>进程与系统指标在同一轮采集中的关联快照。</summary>
public sealed record SystemSnapshot(SystemMemoryMetrics Memory, IReadOnlyList<ProcessSnapshot> Processes);

/// <summary>压力级别是诊断结论，不代表控制器冷却状态。</summary>
public enum PressureLevel
{
    Normal,
    Watch,
    Pressure,
    Critical
}

/// <summary>诊断原因可并存，防止一个总分掩盖压力来源。</summary>
public enum DiagnosticFlag
{
    LowAvailableRam,
    WorkingSetPressure,
    CommitPressure,
    PagingPressure,
    KernelPoolPressure,
    PossibleProcessLeak,
    PossibleDriverLeak,
    PagefileUsageHigh
}

/// <summary>自动控制器状态；Cooldown/Backoff 期间仍保留独立的压力诊断。</summary>
public enum ControllerState
{
    Normal,
    Watch,
    Pressure,
    Critical,
    Cooldown,
    Backoff
}

/// <summary>诊断级别、标记与面向用户的证据摘要。</summary>
public sealed record DiagnosisResult(
    PressureLevel Level,
    IReadOnlySet<DiagnosticFlag> Flags,
    IReadOnlyList<string> Evidence);

/// <summary>有界历史中的单次系统指标和进程 Private Commit 观测。</summary>
public sealed record HistoryEntry(
    DateTimeOffset CapturedAt,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    ulong CommittedBytes,
    ulong CommitLimitBytes,
    ulong PagedPoolBytes,
    ulong NonpagedPoolBytes,
    double? PageReadsPerSecond,
    double? PagesInputPerSecond,
    IReadOnlyList<ProcessCommitObservation> ProcessCommits);

/// <summary>仅保存用于泄漏趋势的进程身份和 Private Commit 数值。</summary>
public readonly record struct ProcessCommitObservation(ProcessIdentity Identity, ulong PrivateCommitBytes);

/// <summary>所有可调诊断、回收和采样阈值的集中配置。</summary>
public sealed record GuardianOptions
{
    public int HistoryCapacity { get; init; } = 720;
    public TimeSpan HistoryWindow { get; init; } = TimeSpan.FromMinutes(60);
    public TimeSpan SystemSampleInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ProcessSampleInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ForegroundPollInterval { get; init; } = TimeSpan.FromSeconds(5);
    public double AvailableWatchRatio { get; init; } = 0.15;
    public double AvailablePressureRatio { get; init; } = 0.08;
    public double AvailableCriticalRatio { get; init; } = 0.03;
    public double AvailableRecoveryRatio { get; init; } = 0.18;
    public double CommitWatchRatio { get; init; } = 0.85;
    public double CommitPressureRatio { get; init; } = 0.92;
    public double CommitCriticalRatio { get; init; } = 0.97;
    public double CommitRecoveryRatio { get; init; } = 0.80;
    public double KernelPoolWatchRatio { get; init; } = 0.20;
    public double PageReadsAlertPerSecond { get; init; } = 5;
    public double PagesInputAlertPerSecond { get; init; } = 50;
    public double PageReadsCriticalPerSecond { get; init; } = 20;
    public ulong ProcessLeakGrowthBytes { get; init; } = 256UL * 1024 * 1024;
    public ulong DriverLeakGrowthBytes { get; init; } = 64UL * 1024 * 1024;
    public TimeSpan LeakWindow { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan PressureConfirmation { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CriticalConfirmation { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RecoveryConfirmation { get; init; } = TimeSpan.FromSeconds(45);
    public ulong MinimumCandidateWorkingSetBytes { get; init; } = 256UL * 1024 * 1024;
    public double MaximumCandidateCpuPercent { get; init; } = 1;
    public double MaximumCandidateIoBytesPerSecond { get; init; } = 64 * 1024;
    public TimeSpan MinimumIdleTime { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan RecentUseProtection { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan GlobalCooldown { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ProcessCooldown { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan FeedbackDelay { get; init; } = TimeSpan.FromSeconds(5);
    public ulong MinimumAvailableIncreaseBytes { get; init; } = 64UL * 1024 * 1024;
    public ulong MeaningfulWorkingSetDropBytes { get; init; } = 64UL * 1024 * 1024;
    public ulong CommitUnchangedToleranceBytes { get; init; } = 16UL * 1024 * 1024;
    public TimeSpan BackoffInitial { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan BackoffMaximum { get; init; } = TimeSpan.FromMinutes(60);
    public double PagingRateIncreaseFactor { get; init; } = 1.5;
    public double PagingRateMinimumIncrease { get; init; } = 5;
    public int MaximumProcessesPerRound { get; init; } = 2;
    public int MaximumTrackedProcesses { get; init; } = 512;
    public double MinimumStrategyScoreForReclaim { get; init; } = 0.25;
    public HashSet<string> Denylist { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "services", "lsass", "winlogon",
        "svchost", "fontdrvhost", "dwm", "MemCompression", "Secure System", "MsMpEng", "SearchIndexer",
        "MemGuardian.Next", "MemGuardian.Next.Cli", "memguardian"
    };
}

/// <summary>Persisted cooldown and adaptive strategy state.</summary>
public sealed record ReclaimState
{
    public PressureLevel ConfirmedPressureLevel { get; init; } = PressureLevel.Normal;
    public PressureLevel? PendingPressureLevel { get; init; }
    public DateTimeOffset? PendingPressureSince { get; init; }
    public DateTimeOffset? GlobalCooldownUntil { get; init; }
    public DateTimeOffset? BackoffUntil { get; init; }
    public int ConsecutiveNegativeOutcomes { get; init; }
    public double StrategyScore { get; init; } = 1;
    public Dictionary<string, DateTimeOffset> ProcessCooldowns { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, DateTimeOffset> LastForegroundUse { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>候选选择结果包含接受与拒绝原因，供 dry-run 与真实回收共用。</summary>
public sealed record CandidateSelection(
    IReadOnlyList<ProcessSnapshot> Candidates,
    IReadOnlyList<CandidateRejection> Rejections,
    string? BlockedBy);

/// <summary>被跳过进程及其安全策略原因。</summary>
public sealed record CandidateRejection(ProcessIdentity Identity, string ProcessName, string Reason);

/// <summary>一次安全回收尝试及其 native 错误。</summary>
public sealed record ReclaimAttempt(ProcessIdentity Identity, string ProcessName, bool Succeeded, string? Error)
{
    /// <summary>True only when EmptyWorkingSet was actually invoked for this process.</summary>
    public bool NativeCallAttempted { get; init; }
}

/// <summary>回收前后测量和策略反馈结果。</summary>
public sealed record ReclaimFeedback(
    ulong WorkingSetReclaimedBytes,
    long AvailableRamDeltaBytes,
    long CommitDeltaBytes,
    double? PageReadsBefore,
    double? PageReadsAfter,
    double? PagesInputBefore,
    double? PagesInputAfter,
    bool PagingObserved,
    bool PagingWorsened,
    bool ResidentPagesOnly,
    bool OutcomeNegative,
    TimeSpan SuggestedCooldown,
    double StrategyScoreDelta,
    bool TargetsObserved,
    string Summary)
{
    public DateTimeOffset CapturedAt { get; init; }
    public ProcessIdentity? TargetIdentity { get; init; }
    public string? TargetProcessName { get; init; }
    public ulong AvailableRamBeforeBytes { get; init; }
    public ulong AvailableRamAfterBytes { get; init; }
    public ulong SystemCommitBeforeBytes { get; init; }
    public ulong SystemCommitAfterBytes { get; init; }
    public ulong TargetWorkingSetBeforeBytes { get; init; }
    public ulong TargetWorkingSetAfterBytes { get; init; }
}

/// <summary>一次 dry-run 或执行后的完整回收轮次结果。</summary>
public sealed record ReclaimRoundResult(
    CandidateSelection Selection,
    IReadOnlyList<ReclaimAttempt> Attempts,
    IReadOnlyList<ReclaimFeedback> Feedbacks,
    bool DryRun);
