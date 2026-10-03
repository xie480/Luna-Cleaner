using MemGuardian.Next.Core;
using Xunit;

namespace MemGuardian.Next.Tests;

/// <summary>Checks fixed-capacity behavior of the persisted trend ring.</summary>
public sealed class HistoryTests
{
    /// <summary>添加超过容量的样本后，只保留最新的固定数量。</summary>
    [Fact]
    public void RingBufferRemainsBoundedAndKeepsNewestSamples()
    {
        var start = DateTimeOffset.UtcNow;
        var buffer = new SystemHistoryBuffer(3, TimeSpan.FromHours(1));
        for (var index = 0; index < 5; index++)
        {
            var metrics = new SystemMemoryMetrics(start + TimeSpan.FromSeconds(index), 8, 4, 2, 8,
                1, 1, 1, null, null, null, 1);
            buffer.Add(new SystemSnapshot(metrics, Array.Empty<ProcessSnapshot>()), 10);
        }

        var result = buffer.Snapshot();
        Assert.Equal(3, result.Count);
        Assert.Equal(start.AddSeconds(2), result[0].CapturedAt);
        Assert.Equal(start.AddSeconds(4), result[^1].CapturedAt);
    }
}
