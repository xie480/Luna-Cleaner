using System.Text.Json;
using MemGuardian.Next.Core;

namespace MemGuardian.Next.Windows;

/// <summary>Stores bounded local trend and feedback state under the current user's LocalAppData.</summary>
public sealed class LocalRuntimeStore
{
    private const int SchemaVersion = 1;
    private const string RuntimeStateFileName = "state.json";
    private const string SettingsFileName = "settings.json";
    private const string RunLockFileName = "run.lock";
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, MaxDepth = 24 };
    private readonly List<string> _warnings = new();

    /// <summary>Creates the user data directory, loads validated settings and restores bounded history.</summary>
    public LocalRuntimeStore()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) throw new InvalidOperationException("LocalAppData is unavailable.");
        DirectoryPath = Path.Combine(localAppData, "MemGuardian.Next");
        Directory.CreateDirectory(DirectoryPath);
        Options = LoadOptions();
        History = new SystemHistoryBuffer(Options.HistoryCapacity, Options.HistoryWindow);
        State = LoadRuntimeState();
    }

    /// <summary>User-local runtime data directory.</summary>
    public string DirectoryPath { get; }

    /// <summary>Validated options loaded from settings.json or conservative built-in defaults.</summary>
    public GuardianOptions Options { get; }

    /// <summary>In-memory recent trend ring restored from state.json.</summary>
    public SystemHistoryBuffer History { get; }

    /// <summary>Cooldowns and adaptive feedback state.</summary>
    public ReclaimState State { get; private set; } = new();

    /// <summary>Non-fatal read/write issues suitable for console diagnostics.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Acquires an exclusive process lock; null means another mutating command is running.</summary>
    public FileStream? TryAcquireRunLock()
    {
        try
        {
            return new FileStream(Path.Combine(DirectoryPath, RunLockFileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            _warnings.Add($"Cannot acquire local run lock: {exception.Message}");
            return null;
        }
    }

    /// <summary>Updates last-foreground-use time and bounds persisted process identity entries.</summary>
    public void RecordForegroundUse(ProcessIdentity identity, DateTimeOffset now)
    {
        var recent = new Dictionary<string, DateTimeOffset>(State.LastForegroundUse, StringComparer.Ordinal)
        {
            [identity.ToKey()] = now
        };
        foreach (var expired in recent.Where(item => item.Value < now - TimeSpan.FromHours(24))
                     .Select(item => item.Key).ToArray()) recent.Remove(expired);
        while (recent.Count > 4096)
        {
            var oldest = recent.MinBy(item => item.Value).Key;
            recent.Remove(oldest);
        }
        State = State with { LastForegroundUse = recent };
    }

    /// <summary>Replaces strategy state and removes expired or excess per-process cooldown records.</summary>
    public void UpdateState(ReclaimState state, DateTimeOffset now)
    {
        var cooldowns = state.ProcessCooldowns.Where(item => item.Value > now)
            .OrderByDescending(item => item.Value).Take(2048)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        State = state with
        {
            ProcessCooldowns = cooldowns,
            LastForegroundUse = new Dictionary<string, DateTimeOffset>(State.LastForegroundUse, StringComparer.Ordinal)
        };
    }

    /// <summary>Atomically persists options, cooldowns and the bounded history buffer.</summary>
    public string? Save()
    {
        try
        {
            WriteJsonAtomic(Path.Combine(DirectoryPath, SettingsFileName), Options);
            var data = new RuntimeStateFile(SchemaVersion, State, History.Snapshot());
            WriteJsonAtomic(Path.Combine(DirectoryPath, RuntimeStateFileName), data);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            var warning = $"Could not persist local trend/strategy state: {exception.Message}";
            _warnings.Add(warning);
            return warning;
        }
    }

    private GuardianOptions LoadOptions()
    {
        var path = Path.Combine(DirectoryPath, SettingsFileName);
        if (!File.Exists(path)) return new GuardianOptions();
        try
        {
            var parsed = JsonSerializer.Deserialize<GuardianOptions>(File.ReadAllText(path), _json);
            if (parsed is null) throw new JsonException("Settings file is empty.");
            var defaults = new GuardianOptions();
            var historyWindow = Clamp(parsed.HistoryWindow, TimeSpan.FromMinutes(15), defaults.HistoryWindow);
            var systemInterval = Clamp(parsed.SystemSampleInterval, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
            var requiredHistoryCapacity = (int)Math.Ceiling(historyWindow.TotalSeconds / systemInterval.TotalSeconds);
            var availableWatch = Math.Clamp(parsed.AvailableWatchRatio, 0.10, 0.35);
            var availablePressure = Math.Clamp(parsed.AvailablePressureRatio, 0.03, Math.Min(0.20, availableWatch - 0.01));
            var availableCritical = Math.Clamp(parsed.AvailableCriticalRatio, 0.005, Math.Min(0.05, availablePressure));
            var availableRecovery = Math.Clamp(parsed.AvailableRecoveryRatio, availableWatch, 0.60);
            var commitWatch = Math.Clamp(parsed.CommitWatchRatio, 0.70, 0.95);
            var commitPressure = Math.Clamp(parsed.CommitPressureRatio, commitWatch + 0.01, 0.98);
            var commitCritical = Math.Clamp(parsed.CommitCriticalRatio, commitPressure + 0.01, 0.999);
            var commitRecovery = Math.Clamp(parsed.CommitRecoveryRatio, 0.50, commitWatch);
            var backoffInitial = Max(parsed.BackoffInitial, TimeSpan.FromMinutes(15));
            var backoffMaximum = Max(Clamp(parsed.BackoffMaximum, TimeSpan.FromMinutes(15), TimeSpan.FromHours(4)), backoffInitial);
            return parsed with
            {
                HistoryCapacity = Math.Clamp(Math.Max(parsed.HistoryCapacity, requiredHistoryCapacity), 2, defaults.HistoryCapacity),
                HistoryWindow = historyWindow,
                LeakWindow = Clamp(parsed.LeakWindow, TimeSpan.FromMinutes(10), historyWindow),
                SystemSampleInterval = systemInterval,
                ProcessSampleInterval = Clamp(parsed.ProcessSampleInterval, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2)),
                ForegroundPollInterval = Clamp(parsed.ForegroundPollInterval, systemInterval, TimeSpan.FromSeconds(30)),
                AvailableWatchRatio = availableWatch,
                AvailablePressureRatio = availablePressure,
                AvailableCriticalRatio = availableCritical,
                AvailableRecoveryRatio = availableRecovery,
                CommitWatchRatio = commitWatch,
                CommitPressureRatio = commitPressure,
                CommitCriticalRatio = commitCritical,
                CommitRecoveryRatio = commitRecovery,
                PressureConfirmation = Clamp(parsed.PressureConfirmation, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2)),
                CriticalConfirmation = Clamp(parsed.CriticalConfirmation, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1)),
                RecoveryConfirmation = Clamp(parsed.RecoveryConfirmation, TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(5)),
                KernelPoolWatchRatio = Math.Clamp(parsed.KernelPoolWatchRatio, 0.05, 0.50),
                PageReadsAlertPerSecond = Math.Clamp(parsed.PageReadsAlertPerSecond, 1, 100),
                PagesInputAlertPerSecond = Math.Clamp(parsed.PagesInputAlertPerSecond, 10, 1000),
                PageReadsCriticalPerSecond = Math.Max(parsed.PageReadsCriticalPerSecond, Math.Clamp(parsed.PageReadsAlertPerSecond, 1, 100)),
                PagingRateIncreaseFactor = Math.Clamp(parsed.PagingRateIncreaseFactor, 1.25, 3),
                PagingRateMinimumIncrease = Math.Clamp(parsed.PagingRateMinimumIncrease, 5, 500),
                RecentUseProtection = Max(parsed.RecentUseProtection, TimeSpan.FromSeconds(120)),
                MinimumIdleTime = Max(parsed.MinimumIdleTime, defaults.MinimumIdleTime),
                MinimumCandidateWorkingSetBytes = Math.Max(parsed.MinimumCandidateWorkingSetBytes, defaults.MinimumCandidateWorkingSetBytes),
                MaximumCandidateCpuPercent = Math.Clamp(parsed.MaximumCandidateCpuPercent, 0, defaults.MaximumCandidateCpuPercent),
                MaximumCandidateIoBytesPerSecond = Math.Clamp(parsed.MaximumCandidateIoBytesPerSecond, 0, defaults.MaximumCandidateIoBytesPerSecond),
                GlobalCooldown = Max(parsed.GlobalCooldown, TimeSpan.FromMinutes(5)),
                ProcessCooldown = Max(parsed.ProcessCooldown, TimeSpan.FromMinutes(15)),
                MinimumAvailableIncreaseBytes = Math.Max(parsed.MinimumAvailableIncreaseBytes, defaults.MinimumAvailableIncreaseBytes),
                MeaningfulWorkingSetDropBytes = Math.Max(parsed.MeaningfulWorkingSetDropBytes, defaults.MeaningfulWorkingSetDropBytes),
                CommitUnchangedToleranceBytes = Math.Max(parsed.CommitUnchangedToleranceBytes, defaults.CommitUnchangedToleranceBytes),
                MaximumProcessesPerRound = Math.Clamp(parsed.MaximumProcessesPerRound, 1, 2),
                MaximumTrackedProcesses = Math.Clamp(parsed.MaximumTrackedProcesses, 1, defaults.MaximumTrackedProcesses),
                MinimumStrategyScoreForReclaim = Math.Clamp(parsed.MinimumStrategyScoreForReclaim,
                    defaults.MinimumStrategyScoreForReclaim, 1),
                BackoffInitial = backoffInitial,
                BackoffMaximum = backoffMaximum,
                FeedbackDelay = Clamp(parsed.FeedbackDelay, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)),
                Denylist = MergeDenylist(defaults.Denylist, parsed.Denylist)
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _warnings.Add($"Settings could not be read; safe defaults are active: {exception.Message}");
            return new GuardianOptions();
        }
    }

    private ReclaimState LoadRuntimeState()
    {
        var path = Path.Combine(DirectoryPath, RuntimeStateFileName);
        if (!File.Exists(path)) return new ReclaimState();
        try
        {
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new JsonException("State file exceeds the size limit.");
            var data = JsonSerializer.Deserialize<RuntimeStateFile>(File.ReadAllText(path), _json);
            if (data is null || data.Version != SchemaVersion) throw new JsonException("Unsupported state schema.");
            var now = DateTimeOffset.UtcNow;
            History.Restore(data.History ?? Array.Empty<HistoryEntry>(), now);
            var state = data.State ?? new ReclaimState();
            return state with
            {
                ProcessCooldowns = (state.ProcessCooldowns ?? new Dictionary<string, DateTimeOffset>()).Where(item => item.Value > now)
                    .OrderByDescending(item => item.Value).Take(2048)
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                LastForegroundUse = (state.LastForegroundUse ?? new Dictionary<string, DateTimeOffset>()).Where(item => item.Value >= now - TimeSpan.FromHours(24))
                    .OrderByDescending(item => item.Value).Take(4096)
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _warnings.Add($"Runtime history could not be read; this session starts without trend data: {exception.Message}");
            return new ReclaimState();
        }
    }

    private void WriteJsonAtomic<T>(string path, T value)
    {
        var temporaryPath = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _json);
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                   64 * 1024, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    private static TimeSpan Max(TimeSpan value, TimeSpan minimum) => value < minimum ? minimum : value;

    private static HashSet<string> MergeDenylist(HashSet<string> required, HashSet<string>? configured)
    {
        var merged = new HashSet<string>(required, StringComparer.OrdinalIgnoreCase);
        if (configured is not null) merged.UnionWith(configured);
        return merged;
    }

    private sealed record RuntimeStateFile(int Version, ReclaimState? State, IReadOnlyList<HistoryEntry>? History);
}
