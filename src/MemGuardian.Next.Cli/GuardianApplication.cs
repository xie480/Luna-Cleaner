using System.Diagnostics;
using System.Runtime.Versioning;
using MemGuardian.Next.Core;
using MemGuardian.Next.Windows;

namespace MemGuardian.Next.Cli;

/// <summary>Coordinates CLI commands while keeping read-only inspection separate from recovery.</summary>
[SupportedOSPlatform("windows")]
internal sealed class GuardianApplication
{
    private readonly DiagnosisEngine _diagnosis = new();
    private readonly ReclaimCandidateSelector _selector = new();

    /// <summary>Executes one parsed command and returns a process exit code.</summary>
    internal async Task<int> ExecuteAsync(CliRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("MemGuardian.Next 需要 Windows 10/11 x64。");
            return 3;
        }

        if (request.Command == CliCommand.Help)
        {
            ConsolePresenter.PrintHelp();
            return 0;
        }

        LocalRuntimeStore store;
        try
        {
            store = new LocalRuntimeStore();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"无法初始化本地状态目录：{exception.Message}");
            return 4;
        }

        foreach (var warning in store.Warnings) Console.Error.WriteLine($"警告：{warning}");
        using var provider = new WindowsSystemSnapshotProvider(() => store.State, store.Options);
        return request.Command switch
        {
            CliCommand.Status => await RunStatusAsync(store, provider, cancellationToken).ConfigureAwait(false),
            CliCommand.Top => await RunTopAsync(request, provider, cancellationToken).ConfigureAwait(false),
            CliCommand.Diagnose => await RunDiagnoseAsync(request, store, provider, cancellationToken).ConfigureAwait(false),
            CliCommand.Run => await RunContinuousAsync(request, store, provider, cancellationToken).ConfigureAwait(false),
            CliCommand.Once => await RunOnceAsync(store, provider, cancellationToken).ConfigureAwait(false),
            _ => 2
        };
    }

    private async Task<int> RunStatusAsync(LocalRuntimeStore store, WindowsSystemSnapshotProvider provider,
        CancellationToken cancellationToken)
    {
        using var lease = TryAcquireLease(store);
        if (lease is null) return 4;
        var stateMachine = new MemoryStateMachine(store.State);
        RecordForegroundUse(store, DateTimeOffset.UtcNow);
        var first = await provider.CaptureAsync(true, cancellationToken).ConfigureAwait(false);
        store.History.Add(first, store.Options.MaximumTrackedProcesses);
        var snapshot = first;
        if (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            RecordForegroundUse(store, DateTimeOffset.UtcNow);
            snapshot = await provider.CaptureAsync(true, cancellationToken).ConfigureAwait(false);
            store.History.Add(snapshot, store.Options.MaximumTrackedProcesses);
        }
        var diagnosis = _diagnosis.Analyze(snapshot, store.History.Snapshot(), store.Options, stateMachine.ConfirmedLevel);
        var state = stateMachine.Observe(diagnosis.Level, snapshot.Memory.CapturedAt, store.Options, store.State);
        store.UpdateState(stateMachine.ExportState(store.State), snapshot.Memory.CapturedAt);
        ConsolePresenter.PrintStatus(snapshot, diagnosis, state, store.History.Snapshot());
        PrintCounterWarnings(provider);
        PrintSaveWarning(store.Save());
        return 0;
    }

    private static async Task<int> RunTopAsync(CliRequest request, WindowsSystemSnapshotProvider provider,
        CancellationToken cancellationToken)
    {
        _ = await provider.CaptureAsync(true, cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        var snapshot = await provider.CaptureAsync(true, cancellationToken).ConfigureAwait(false);
        ConsolePresenter.PrintTop(snapshot, request.TopSort);
        PrintCounterWarnings(provider);
        return 0;
    }

    private async Task<int> RunDiagnoseAsync(CliRequest request, LocalRuntimeStore store,
        WindowsSystemSnapshotProvider provider, CancellationToken cancellationToken)
    {
        using var lease = TryAcquireLease(store);
        if (lease is null) return 4;
        var stateMachine = new MemoryStateMachine(store.State);
        var started = Stopwatch.StartNew();
        var nextProcessSample = DateTimeOffset.MinValue;
        SystemSnapshot? snapshot = null;
        DiagnosisResult? diagnosis = null;
        ControllerState state = ControllerState.Normal;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            RecordForegroundUse(store, now);
            var refreshProcesses = snapshot is null || now >= nextProcessSample;
            snapshot = await provider.CaptureAsync(refreshProcesses, cancellationToken).ConfigureAwait(false);
            if (refreshProcesses) nextProcessSample = now + store.Options.ProcessSampleInterval;
            store.History.Add(snapshot, refreshProcesses ? store.Options.MaximumTrackedProcesses : 0);
            diagnosis = _diagnosis.Analyze(snapshot, store.History.Snapshot(), store.Options, stateMachine.ConfirmedLevel);
            state = stateMachine.Observe(diagnosis.Level, now, store.Options, store.State);
            store.UpdateState(stateMachine.ExportState(store.State), now);
            if (started.Elapsed.TotalSeconds >= request.DurationSeconds) break;
            var remaining = TimeSpan.FromSeconds(request.DurationSeconds) - started.Elapsed;
            await Task.Delay(remaining < store.Options.SystemSampleInterval ? remaining : store.Options.SystemSampleInterval,
                cancellationToken).ConfigureAwait(false);
        } while (started.Elapsed.TotalSeconds <= request.DurationSeconds);

        ConsolePresenter.PrintStatus(snapshot!, diagnosis!, state, store.History.Snapshot());
        Console.WriteLine($"观察窗口：{request.DurationSeconds} 秒。趋势告警需至少覆盖配置的 10 分钟窗口；窗口不足时不会报泄漏。");
        PrintCounterWarnings(provider);
        PrintSaveWarning(store.Save());
        return 0;
    }

    private async Task<int> RunOnceAsync(LocalRuntimeStore store, WindowsSystemSnapshotProvider provider,
        CancellationToken cancellationToken)
    {
        using var lease = TryAcquireLease(store);
        if (lease is null) return 4;
        var stateMachine = new MemoryStateMachine(store.State);
        var started = Stopwatch.StartNew();
        var maximumWait = store.Options.PressureConfirmation + store.Options.SystemSampleInterval +
                          store.Options.CriticalConfirmation;
        SystemSnapshot? snapshot = null;
        DiagnosisResult? diagnosis = null;
        ControllerState state = ControllerState.Normal;
        var nextProcessSample = DateTimeOffset.MinValue;
        while (started.Elapsed <= maximumWait)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            RecordForegroundUse(store, now);
            var refreshProcesses = snapshot is null || now >= nextProcessSample;
            snapshot = await provider.CaptureAsync(refreshProcesses, cancellationToken).ConfigureAwait(false);
            if (refreshProcesses) nextProcessSample = now + store.Options.ProcessSampleInterval;
            store.History.Add(snapshot, refreshProcesses ? store.Options.MaximumTrackedProcesses : 0);
            diagnosis = _diagnosis.Analyze(snapshot, store.History.Snapshot(), store.Options, stateMachine.ConfirmedLevel);
            state = stateMachine.Observe(diagnosis.Level, now, store.Options, store.State);
            store.UpdateState(stateMachine.ExportState(store.State), now);
            if (state is ControllerState.Pressure or ControllerState.Critical or ControllerState.Cooldown or ControllerState.Backoff)
                break;
            var remaining = maximumWait - started.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < store.Options.SystemSampleInterval ? remaining : store.Options.SystemSampleInterval,
                cancellationToken).ConfigureAwait(false);
        }

        if (snapshot is null || diagnosis is null) return 4;
        ConsolePresenter.PrintStatus(snapshot, diagnosis, state, store.History.Snapshot());
        await ConsiderReclaimAsync(snapshot, state, store.State, store, provider, dryRun: false, cancellationToken)
            .ConfigureAwait(false);
        PrintCounterWarnings(provider);
        PrintSaveWarning(store.Save());
        return 0;
    }

    private async Task<int> RunContinuousAsync(CliRequest request, LocalRuntimeStore store,
        WindowsSystemSnapshotProvider provider, CancellationToken cancellationToken)
    {
        using var lease = TryAcquireLease(store);
        if (lease is null) return 4;
        var stateMachine = new MemoryStateMachine(store.State);
        var reclaimer = new WindowsWorkingSetReclaimer();
        var coordinator = new AdaptiveReclaimCoordinator(reclaimer, provider, new ReclaimFeedbackEvaluator(), _selector);
        var nextProcessSample = DateTimeOffset.MinValue;
        var nextForegroundPoll = DateTimeOffset.MinValue;
        var nextPersist = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        var nextPolicyReport = DateTimeOffset.MinValue;
        ControllerState? lastReportedState = null;
        PressureLevel? lastReportedLevel = null;
        Console.WriteLine(request.DryRun ? "持续监控已启动（Dry-run）；不会回收 Working Set。按 Ctrl+C 退出。" :
            "持续监控与自适应回收已启动；按 Ctrl+C 退出。");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                if (now >= nextForegroundPoll)
                {
                    RecordForegroundUse(store, now);
                    nextForegroundPoll = now + store.Options.ForegroundPollInterval;
                }

                var refreshProcesses = now >= nextProcessSample;
                var snapshot = await provider.CaptureAsync(refreshProcesses, cancellationToken).ConfigureAwait(false);
                if (refreshProcesses) nextProcessSample = now + store.Options.ProcessSampleInterval;
                store.History.Add(snapshot, refreshProcesses ? store.Options.MaximumTrackedProcesses : 0);
                var history = store.History.Snapshot();
                var diagnosis = _diagnosis.Analyze(snapshot, history, store.Options, stateMachine.ConfirmedLevel);
                var state = stateMachine.Observe(diagnosis.Level, now, store.Options, store.State);
                store.UpdateState(stateMachine.ExportState(store.State), now);
                if (state != lastReportedState || diagnosis.Level != lastReportedLevel)
                {
                    Console.WriteLine($"{now.ToLocalTime():HH:mm:ss} State={state}, diagnosis={diagnosis.Level}, Available={ConsolePresenter.Bytes(snapshot.Memory.AvailablePhysicalBytes)}, Commit={ConsolePresenter.Percent(snapshot.Memory.CommitRatio)}");
                    ConsolePresenter.PrintDiagnosis(diagnosis);
                    lastReportedState = state;
                    lastReportedLevel = diagnosis.Level;
                }

                if (state is ControllerState.Pressure or ControllerState.Critical)
                {
                    var reportPolicy = DateTimeOffset.UtcNow >= nextPolicyReport;
                    if (reportPolicy) nextPolicyReport = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
                    await ConsiderReclaimAsync(snapshot, state, store.State, store, provider, request.DryRun,
                        cancellationToken, coordinator, reportPolicy).ConfigureAwait(false);
                }

                if (DateTimeOffset.UtcNow >= nextPersist)
                {
                    PrintSaveWarning(store.Save());
                    nextPersist = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
                }
                await Task.Delay(store.Options.SystemSampleInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            PrintSaveWarning(store.Save());
        }
        return 0;
    }

    private async Task ConsiderReclaimAsync(SystemSnapshot snapshot, ControllerState state, ReclaimState reclaimState,
        LocalRuntimeStore store, WindowsSystemSnapshotProvider provider, bool dryRun, CancellationToken cancellationToken,
        AdaptiveReclaimCoordinator? coordinator = null, bool reportPolicy = true)
    {
        var now = snapshot.Memory.CapturedAt;
        var selection = _selector.Select(snapshot.Processes, state, reclaimState, store.Options, now);
        if (dryRun)
        {
            if (reportPolicy) ConsolePresenter.PrintCandidates(selection, dryRun: true);
            return;
        }

        if (selection.Candidates.Count == 0)
        {
            if (selection.BlockedBy is null && reportPolicy) ConsolePresenter.PrintCandidates(selection, dryRun: false);
            return;
        }

        coordinator ??= new AdaptiveReclaimCoordinator(new WindowsWorkingSetReclaimer(), provider,
            new ReclaimFeedbackEvaluator(), _selector);
        var result = await coordinator.ExecuteAsync(snapshot, selection, state, reclaimState, false,
            store.Options, cancellationToken).ConfigureAwait(false);
        ConsolePresenter.PrintRound(result);
        store.UpdateState(AdaptiveReclaimStateUpdater.ApplyRound(store.State, result, store.Options,
            snapshot.Memory.CapturedAt), snapshot.Memory.CapturedAt);
        PrintSaveWarning(store.Save());
    }

    private static FileStream? TryAcquireLease(LocalRuntimeStore store)
    {
        var lease = store.TryAcquireRunLock();
        if (lease is null) Console.Error.WriteLine("另一个会修改趋势或回收状态的命令正在运行，或本地锁不可用；本次操作已停止。");
        return lease;
    }

    private static void RecordForegroundUse(LocalRuntimeStore store, DateTimeOffset now)
    {
        var identity = WindowsForegroundTracker.GetForegroundIdentity();
        if (identity is not null) store.RecordForegroundUse(identity.Value, now);
    }

    private static void PrintCounterWarnings(WindowsSystemSnapshotProvider provider)
    {
        foreach (var error in provider.CounterErrors) Console.Error.WriteLine($"PDH 提示：{error}");
    }

    private static void PrintSaveWarning(string? warning)
    {
        if (warning is not null) Console.Error.WriteLine($"警告：{warning}");
    }
}
