namespace MemGuardian.Next.Core;

/// <summary>通过持续时间确认和恢复迟滞管理内存压力状态。</summary>
public sealed class MemoryStateMachine
{
    private PressureLevel _level;
    private PressureLevel? _candidate;
    private DateTimeOffset? _candidateSince;

    /// <summary>Restores confirmed and pending pressure state for one-shot CLI continuity.</summary>
    public MemoryStateMachine(ReclaimState? persistedState = null)
    {
        _level = persistedState?.ConfirmedPressureLevel ?? PressureLevel.Normal;
        _candidate = persistedState?.PendingPressureLevel;
        _candidateSince = persistedState?.PendingPressureSince;
    }

    /// <summary>当前确认后的压力级别。</summary>
    public PressureLevel ConfirmedLevel => _level;

    /// <summary>Copies confirmed and pending state into the persisted control record.</summary>
    public ReclaimState ExportState(ReclaimState current) => current with
    {
        ConfirmedPressureLevel = _level,
        PendingPressureLevel = _candidate,
        PendingPressureSince = _candidateSince
    };

    /// <summary>推进状态机；Backoff/Cooldown 覆盖回收控制状态但不丢失压力级别。</summary>
    public ControllerState Observe(PressureLevel requested, DateTimeOffset now, GuardianOptions options,
        ReclaimState reclaimState)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reclaimState);

        UpdateConfirmedLevel(requested, now, options);
        if (reclaimState.BackoffUntil is { } backoff && now < backoff) return ControllerState.Backoff;
        if (reclaimState.GlobalCooldownUntil is { } cooldown && now < cooldown) return ControllerState.Cooldown;
        return _level switch
        {
            PressureLevel.Watch => ControllerState.Watch,
            PressureLevel.Pressure => ControllerState.Pressure,
            PressureLevel.Critical => ControllerState.Critical,
            _ => ControllerState.Normal
        };
    }

    private void UpdateConfirmedLevel(PressureLevel requested, DateTimeOffset now, GuardianOptions options)
    {
        if (_candidateSince is { } pendingSince && now - pendingSince > TimeSpan.FromMinutes(1))
        {
            _candidate = null;
            _candidateSince = null;
        }

        if (requested == _level)
        {
            _candidate = null;
            _candidateSince = null;
            return;
        }

        var movingUp = (int)requested > (int)_level;
        if (_candidate != requested)
        {
            _candidate = requested;
            _candidateSince = now;
            return;
        }

        var required = movingUp
            ? requested == PressureLevel.Critical ? options.CriticalConfirmation : options.PressureConfirmation
            : options.RecoveryConfirmation;
        if (_candidateSince is { } since && now - since >= required)
        {
            _level = requested;
            _candidate = null;
            _candidateSince = null;
        }
    }
}
