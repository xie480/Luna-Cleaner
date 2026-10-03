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
    private readonly object _reclaimRequestSync = new();
    private TaskCompletionSource<string>? _pendingManualReclaim;
    private bool _reclaimInProgress;
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

    /// <summary>Queues a user-triggered round on the monitor worker using the same pressure and safety gates as automation.</summary>
    public Task<string> RequestManualReclaimAsync()
    {
        if (!IsRunning) return Task.FromResult("监控尚未运行，请先启动监控后再清理。");
        var request = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_reclaimRequestSync)
        {
            if (_pendingManualReclaim is not null || _reclaimInProgress)
                return Task.FromResult("已有回收或安全检查正在执行，请稍后再试。");
            _pendingManualReclaim = request;
        }

        try
        {
            RequestRefresh();
            return request.Task;
        }
        catch (ObjectDisposedException)
        {
            lock (_reclaimRequestSync)
                if (ReferenceEquals(_pendingManualReclaim, request)) _pendingManualReclaim = null;
            return Task.FromResult("监控已停止，未执行清理。");
        }
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
                bool manualPending;
                lock (_reclaimRequestSync) manualPending = _pendingManualReclaim is not null;
                var refreshProcesses = manualPending || now >= nextProcessSample;
                var snapshot = await provider.CaptureAsync(refreshProcesses, cancellationToken).ConfigureAwait(false);
                if (refreshProcesses) nextProcessSample = now + _store.Options.ProcessSampleInterval;
                _store.History.Add(snapshot, refreshProcesses ? _store.Options.MaximumTrackedProcesses : 0);

                var diagnosis = _diagnosis.Analyze(snapshot, _store.History.Snapshot(), _store.Options,
                    stateMachine.ConfirmedLevel);
                var state = stateMachine.Observe(diagnosis.Level, now, _store.Options, _store.State);
                _store.UpdateState(stateMachine.ExportState(_store.State), now);
                var selection = _selector.Select(snapshot.Processes, state, _store.State, _store.Options, now);
                ReclaimRoundResult? round = null;
                TaskCompletionSource<string>? manualRequest;
                bool automaticRound;
                lock (_reclaimRequestSync)
                {
                    var deferManualUntilFreshProcessSample = _pendingManualReclaim is not null && !refreshProcesses;
                    manualRequest = deferManualUntilFreshProcessSample ? null : _pendingManualReclaim;
                    if (!deferManualUntilFreshProcessSample) _pendingManualReclaim = null;
                    automaticRound = !deferManualUntilFreshProcessSample && manualRequest is null && Volatile.Read(ref _automaticReclaim) == 1 &&
                                     state is (ControllerState.Pressure or ControllerState.Critical) &&
                                     selection.Candidates.Count > 0;
                    _reclaimInProgress = !deferManualUntilFreshProcessSample && (manualRequest is not null || automaticRound);
                }

                string? manualMessage = null;
                try
                {
                    if (manualRequest is not null && state is not (ControllerState.Pressure or ControllerState.Critical))
                    {
                        const string reason = "当前尚未确认 Pressure / Critical；为避免无效回收，本次手动请求已跳过。";
                        _store.RecordReclaimLog(ReclaimLogBuilder.Skipped(ReclaimTrigger.Manual, DateTimeOffset.UtcNow, reason));
                        manualMessage = reason;
                        AppendSaveWarningToManualMessage(ref manualMessage);
                    }
                    else if (manualRequest is not null && selection.Candidates.Count == 0)
                    {
                        var rawReason = selection.BlockedBy ?? selection.Rejections.FirstOrDefault()?.Reason;
                        var reason = rawReason is null ? "当前没有符合安全条件的进程候选。" : TranslateReclaimReason(rawReason);
                        reason = $"本次手动清理已安全跳过：{reason}";
                        _store.RecordReclaimLog(ReclaimLogBuilder.Skipped(ReclaimTrigger.Manual, DateTimeOffset.UtcNow, reason));
                        manualMessage = reason;
                        AppendSaveWarningToManualMessage(ref manualMessage);
                    }
                    else if (automaticRound || manualRequest is not null)
                    {
                        round = await coordinator.ExecuteAsync(snapshot, selection, state, _store.State,
                            dryRun: false, _store.Options, cancellationToken).ConfigureAwait(false);
                        var trigger = manualRequest is null ? ReclaimTrigger.Automatic : ReclaimTrigger.Manual;
                        RecordRound(round, trigger);
                        if (manualRequest is not null)
                            manualMessage = FormatManualResult(_store.RecentReclaimLogs.TakeLast(round.Attempts.Count).ToArray());
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (manualRequest is not null) manualMessage = "监控正在停止，清理请求已中止。";
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                   Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    if (manualRequest is not null)
                    {
                        manualMessage = $"清理过程发生错误：{exception.Message}";
                        _store.RecordReclaimLog(ReclaimLogBuilder.Failed(ReclaimTrigger.Manual, DateTimeOffset.UtcNow,
                            $"手动清理未完成：{exception.Message}"));
                        _store.Save();
                    }
                    throw;
                }
                finally
                {
                    lock (_reclaimRequestSync) _reclaimInProgress = false;
                    manualRequest?.TrySetResult(manualMessage ?? "本次清理请求已处理，请查看清理日志获取结果。");
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
                CompleteQueuedManualRequestAfterSamplingError(exception);
                Publish(Latest with { Message = $"采样遇到暂时性错误：{exception.Message}", UpdatedAt = DateTimeOffset.UtcNow });
            }

            if (cancellationToken.IsCancellationRequested) break;
            await WaitForNextSampleAsync(_store.Options.SystemSampleInterval, cancellationToken).ConfigureAwait(false);
        }

        var finalWarning = _store.Save();
        if (finalWarning is not null) Publish(Latest with { Message = finalWarning, UpdatedAt = DateTimeOffset.UtcNow });
        TaskCompletionSource<string>? pending;
        lock (_reclaimRequestSync)
        {
            pending = _pendingManualReclaim;
            _pendingManualReclaim = null;
            _reclaimInProgress = false;
        }
        pending?.TrySetResult("监控已停止，未执行清理。");
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

    private void RecordRound(ReclaimRoundResult round, ReclaimTrigger trigger)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var feedback in round.Feedbacks) _store.RecordFeedback(feedback);
        foreach (var entry in ReclaimLogBuilder.FromRound(round, trigger, now)) _store.RecordReclaimLog(entry);
        _store.UpdateState(AdaptiveReclaimStateUpdater.ApplyRound(_store.State, round, _store.Options, now), now);
        var warning = _store.Save();
        if (warning is not null) Publish(Latest with { Message = warning, UpdatedAt = now });
    }

    private static string FormatManualResult(IReadOnlyList<ReclaimLogEntry> entries)
    {
        if (entries.Count == 0) return "没有进程通过重新验证；清理已安全跳过。";
        var succeeded = entries.Count(entry => entry.Status == ReclaimLogStatus.Succeeded);
        if (succeeded > 0)
            return entries.LastOrDefault(entry => entry.Status == ReclaimLogStatus.Succeeded)?.Summary ?? "回收请求已执行，请查看清理日志。";
        var latest = entries[^1];
        return latest.Status == ReclaimLogStatus.Failed
            ? $"清理未成功：{latest.Summary}"
            : $"清理已安全跳过：{latest.Summary}";
    }

    private void CompleteQueuedManualRequestAfterSamplingError(Exception exception)
    {
        TaskCompletionSource<string>? request;
        lock (_reclaimRequestSync)
        {
            request = _pendingManualReclaim;
            _pendingManualReclaim = null;
        }
        if (request is null) return;

        var message = $"系统采样失败，未执行清理：{exception.Message}";
        _store.RecordReclaimLog(ReclaimLogBuilder.Failed(ReclaimTrigger.Manual, DateTimeOffset.UtcNow, message));
        _store.Save();
        request.TrySetResult(message);
    }

    private void AppendSaveWarningToManualMessage(ref string? message)
    {
        var warning = _store.Save();
        if (warning is null) return;
        message = $"{message} 日志保存警告：{warning}";
        Publish(Latest with { Message = warning, UpdatedAt = DateTimeOffset.UtcNow });
    }

    private static string TranslateReclaimReason(string reason) => reason switch
    {
        "Pressure/Critical not confirmed" => "压力状态尚未确认",
        "Global cooldown active" => "全局冷却中",
        "Backoff active" => "自适应退避中",
        "Foreground process" => "前台程序保护",
        "Used within the protected interval" => "最近使用保护",
        "Not idle long enough" => "闲置时间不足",
        "CPU activity is high or unknown" => "CPU 活跃或指标未知",
        "I/O activity is high or unknown" => "I/O 活跃或指标未知",
        "Process cooldown active" => "该进程仍在冷却期",
        "Denylist" => "denylist 保护",
        "Not owned by current user" => "非当前用户进程",
        "Not in current user session" => "非当前 Session",
        "Foreground-use history unknown" => "前台使用历史未知",
        _ => reason
    };

    private static void RecordForegroundUse(LocalRuntimeStore store, DateTimeOffset now)
    {
        var identity = WindowsForegroundTracker.GetForegroundIdentity();
        if (identity is not null) store.RecordForegroundUse(identity.Value, now);
    }
}
