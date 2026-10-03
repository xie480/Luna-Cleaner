using System.ComponentModel;
using System.IO;
using System.Runtime.Versioning;
using MemGuardian.Next.Core;
using MemGuardian.Next.Windows;

namespace MemGuardian.Next.Desktop;

public sealed record MonitorUpdate(
    SystemSnapshot? Snapshot,
    DiagnosisResult? Diagnosis,
    ControllerState State,
    CandidateSelection? Candidates,
    ReclaimRoundResult? ReclaimResult,
    string? Message,
    DateTimeOffset UpdatedAt);

/// <summary>Bounded cadence monitor that reuses core diagnosis, candidate safety and adaptive feedback.</summary>
[SupportedOSPlatform("windows")]
public sealed class GuardianMonitorService : IAsyncDisposable
{
    private readonly LocalRuntimeStore _store;
    private readonly DiagnosisEngine _diagnosis = new();
    private readonly ReclaimCandidateSelector _selector = new();
    private readonly CancellationTokenSource _stop = new();
    private FileStream? _lease;
    private WindowsSystemSnapshotProvider? _provider;
    private AdaptiveReclaimCoordinator? _coordinator;
    private Task? _worker;
    private int _automaticReclaim;
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private MonitorUpdate _latest = new(null, null, ControllerState.Normal, null, null,
        "正在等待首轮采样。", DateTimeOffset.UtcNow);

    public GuardianMonitorService(LocalRuntimeStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public event EventHandler<MonitorUpdate>? Updated;
    public MonitorUpdate Latest => Volatile.Read(ref _latest);
    public bool IsRunning => _worker is { IsCompleted: false };

    public void Start(bool automaticReclaim)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("桌面端仅支持 Windows 10/11 x64。");
        if (_worker is not null) throw new InvalidOperationException("监控服务已经启动。");
        _lease = _store.TryAcquireRunLock() ?? throw new IOException("另一个 MemGuardian 实例正在修改状态，请稍后重试。");
        try
        {
            _provider = new WindowsSystemSnapshotProvider(() => _store.State, _store.Options);
            _coordinator = new AdaptiveReclaimCoordinator(new WindowsWorkingSetReclaimer(), _provider,
                new ReclaimFeedbackEvaluator(), _selector);
            SetAutomaticReclaim(automaticReclaim);
            _worker = Task.Run(() => RunAsync(_stop.Token));
        }
        catch
        {
            _provider?.Dispose();
            _provider = null;
            _lease.Dispose();
            _lease = null;
            throw;
        }
    }

    public void SetAutomaticReclaim(bool enabled) => Interlocked.Exchange(ref _automaticReclaim, enabled ? 1 : 0);

    public void RequestRefresh()
    {
        try { _refreshSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _provider?.Dispose();
        _provider = null;
        _lease?.Dispose();
        _lease = null;
        _refreshSignal.Dispose();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var provider = _provider!;
        var coordinator = _coordinator!;
        var stateMachine = new MemoryStateMachine(_store.State);
        var nextProcessSample = DateTimeOffset.MinValue;
        var nextPersist = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                RecordForegroundUse(_store, now);
                var refreshProcesses = now >= nextProcessSample;
                var snapshot = await provider.CaptureAsync(refreshProcesses, cancellationToken).ConfigureAwait(false);
                if (refreshProcesses) nextProcessSample = now + _store.Options.ProcessSampleInterval;
                _store.History.Add(snapshot, refreshProcesses ? _store.Options.MaximumTrackedProcesses : 0);

                var diagnosis = _diagnosis.Analyze(snapshot, _store.History.Snapshot(), _store.Options,
                    stateMachine.ConfirmedLevel);
                var state = stateMachine.Observe(diagnosis.Level, now, _store.Options, _store.State);
                _store.UpdateState(stateMachine.ExportState(_store.State), now);
                var selection = _selector.Select(snapshot.Processes, state, _store.State, _store.Options, now);
                ReclaimRoundResult? round = null;
                if (Volatile.Read(ref _automaticReclaim) == 1 && state is (ControllerState.Pressure or ControllerState.Critical) &&
                    selection.Candidates.Count > 0)
                {
                    round = await coordinator.ExecuteAsync(snapshot, selection, state, _store.State,
                        dryRun: false, _store.Options, cancellationToken).ConfigureAwait(false);
                    foreach (var feedback in round.Feedbacks) _store.RecordFeedback(feedback);
                    _store.UpdateState(AdaptiveReclaimStateUpdater.ApplyRound(_store.State, round, _store.Options,
                        DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
                    var feedbackSaveWarning = _store.Save();
                    if (feedbackSaveWarning is not null)
                        Publish(Latest with { Message = feedbackSaveWarning, UpdatedAt = DateTimeOffset.UtcNow });
                }

                Publish(new MonitorUpdate(snapshot, diagnosis, state, selection, round,
                    round?.Feedbacks.LastOrDefault()?.Summary, DateTimeOffset.UtcNow));
                if (DateTimeOffset.UtcNow >= nextPersist)
                {
                    var warning = _store.Save();
                    if (warning is not null) Publish(Latest with { Message = warning, UpdatedAt = DateTimeOffset.UtcNow });
                    nextPersist = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               Win32Exception or InvalidOperationException or NotSupportedException)
            {
                Publish(Latest with { Message = $"采样遇到暂时性错误：{exception.Message}", UpdatedAt = DateTimeOffset.UtcNow });
            }

            if (cancellationToken.IsCancellationRequested) break;
            await WaitForNextSampleAsync(_store.Options.SystemSampleInterval, cancellationToken).ConfigureAwait(false);
        }

        var finalWarning = _store.Save();
        if (finalWarning is not null) Publish(Latest with { Message = finalWarning, UpdatedAt = DateTimeOffset.UtcNow });
    }

    private async Task WaitForNextSampleAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(interval, waitCancellation.Token);
        var refresh = _refreshSignal.WaitAsync(waitCancellation.Token);
        await Task.WhenAny(delay, refresh).ConfigureAwait(false);
        waitCancellation.Cancel();
        try { await Task.WhenAll(delay, refresh).ConfigureAwait(false); }
        catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested) { }
    }

    private void Publish(MonitorUpdate update)
    {
        Volatile.Write(ref _latest, update);
        Updated?.Invoke(this, update);
    }

    private static void RecordForegroundUse(LocalRuntimeStore store, DateTimeOffset now)
    {
        var identity = WindowsForegroundTracker.GetForegroundIdentity();
        if (identity is not null) store.RecordForegroundUse(identity.Value, now);
    }
}
