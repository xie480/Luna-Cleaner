namespace MemGuardian.Next.Core;

/// <summary>固定容量、固定时间窗口的系统和 Private Commit 趋势环形缓冲区。</summary>
public sealed class SystemHistoryBuffer
{
    private readonly object _sync = new();
    private readonly HistoryEntry?[] _entries;
    private readonly TimeSpan _window;
    private int _start;
    private int _count;

    /// <summary>创建容量有界的历史缓冲区。</summary>
    public SystemHistoryBuffer(int capacity, TimeSpan window)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _entries = new HistoryEntry[capacity];
        _window = window;
    }

    /// <summary>追加系统快照并丢弃超出时间窗口或容量的旧样本。</summary>
    public void Add(SystemSnapshot snapshot, int maximumTrackedProcesses)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var observations = snapshot.Processes
            .OrderByDescending(process => process.PrivateCommitBytes)
            .Take(Math.Max(0, maximumTrackedProcesses))
            .Select(process => new ProcessCommitObservation(process.Identity, process.PrivateCommitBytes))
            .ToArray();
        var memory = snapshot.Memory;
        var entry = new HistoryEntry(memory.CapturedAt, memory.TotalPhysicalBytes, memory.AvailablePhysicalBytes,
            memory.CommittedBytes, memory.CommitLimitBytes, memory.PagedPoolBytes, memory.NonpagedPoolBytes,
            memory.PageReadsPerSecond, memory.PagesInputPerSecond, observations);

        lock (_sync)
        {
            while (_count > 0 && FirstUnsafe()!.CapturedAt < memory.CapturedAt - _window) RemoveFirstUnsafe();
            if (_count == _entries.Length) RemoveFirstUnsafe();
            _entries[(_start + _count) % _entries.Length] = entry;
            _count++;
        }
    }

    /// <summary>返回时间有序的历史副本，调用方可安全枚举。</summary>
    public IReadOnlyList<HistoryEntry> Snapshot()
    {
        lock (_sync)
        {
            var result = new HistoryEntry[_count];
            for (var index = 0; index < _count; index++) result[index] = _entries[(_start + index) % _entries.Length]!;
            return result;
        }
    }

    /// <summary>恢复持久化样本；超出当前配置窗口和容量的旧数据会被忽略。</summary>
    public void Restore(IEnumerable<HistoryEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_sync)
        {
            Array.Clear(_entries);
            _start = 0;
            _count = 0;
        }
        foreach (var entry in entries.Where(item => item is not null).OrderBy(item => item.CapturedAt))
        {
            if (entry is null || entry.ProcessCommits is null) continue;
            if (entry.CapturedAt < now - _window || entry.CapturedAt > now + TimeSpan.FromMinutes(1)) continue;
            var processes = entry.ProcessCommits.Take(2048).ToArray();
            lock (_sync)
            {
                if (_count == _entries.Length) RemoveFirstUnsafe();
                _entries[(_start + _count) % _entries.Length] = entry with { ProcessCommits = processes };
                _count++;
            }
        }
    }

    private HistoryEntry? FirstUnsafe() => _count == 0 ? null : _entries[_start];

    private void RemoveFirstUnsafe()
    {
        _entries[_start] = null;
        _start = (_start + 1) % _entries.Length;
        _count--;
    }
}
