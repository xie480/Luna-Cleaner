using MemGuardian.Next.Desktop;
using MemGuardian.Next.Core;
using MemGuardian.Next.Windows;
using System.IO;
using Xunit;

namespace MemGuardian.Next.Tests;

public sealed class DesktopTests
{
    [Fact]
    public void StartupCommandBuilder_QuotesAppHostAndCanStartHidden()
    {
        var command = StartupCommandBuilder.Build(@"C:\Program Files\MemGuardian.Next\MemGuardian.Next.Desktop.exe",
            startMinimized: true);

        Assert.Equal("\"C:\\Program Files\\MemGuardian.Next\\MemGuardian.Next.Desktop.exe\" --background", command);
    }

    [Fact]
    public void StartupCommandBuilder_IncludesEntryAssemblyForDotnetHost()
    {
        var command = StartupCommandBuilder.Build(@"C:\Program Files\dotnet\dotnet.exe",
            @"C:\Users\Test User\MemGuardian.Next.Desktop.dll", startMinimized: false);

        Assert.Equal("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\Users\\Test User\\MemGuardian.Next.Desktop.dll\"", command);
    }

    [Fact]
    public void DesktopPreferencesStore_RoundTripsWithoutEnablingReclaimByDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MemGuardian.Next.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DesktopPreferencesStore(directory);
            Assert.False(store.Load().AutomaticReclaim);
            var preferences = new DesktopPreferences
            {
                MonitorOnLaunch = true,
                AutomaticReclaim = true,
                RunInBackground = true,
                StartMinimized = true
            };
            store.Save(preferences);

            Assert.Equal(preferences, store.Load());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SettingsNormalization_PreservesMinimumCooldownAndLeakSignalFloors()
    {
        var options = LocalRuntimeStore.NormalizeOptions(new GuardianOptions
        {
            GlobalCooldown = TimeSpan.FromMinutes(1),
            ProcessCooldown = TimeSpan.FromMinutes(2),
            MinimumIdleTime = TimeSpan.FromMinutes(1),
            ProcessLeakGrowthBytes = 1,
            DriverLeakGrowthBytes = 1,
            MaximumProcessesPerRound = 20
        });

        Assert.Equal(TimeSpan.FromMinutes(5), options.GlobalCooldown);
        Assert.Equal(TimeSpan.FromMinutes(15), options.ProcessCooldown);
        Assert.Equal(TimeSpan.FromMinutes(10), options.MinimumIdleTime);
        Assert.Equal(32UL * 1024 * 1024, options.ProcessLeakGrowthBytes);
        Assert.Equal(16UL * 1024 * 1024, options.DriverLeakGrowthBytes);
        Assert.Equal(2, options.MaximumProcessesPerRound);
    }

    [Fact]
    public void RuntimeStore_PersistsBoundedBeforeAndAfterRecoveryMeasurements()
    {
        const ulong mib = 1024 * 1024;
        var root = Path.Combine(Path.GetTempPath(), "MemGuardian.Next.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRuntimeStore(root);
            var now = DateTimeOffset.UtcNow;
            var before = new SystemMemoryMetrics(now, 8 * 1024 * mib, 1024 * mib, 2048 * mib, 8 * 1024 * mib,
                512 * mib, 128 * mib, 256 * mib, 2, 20, 35, 4096 * mib);
            var after = before with
            {
                CapturedAt = now + TimeSpan.FromSeconds(5),
                AvailablePhysicalBytes = 1280 * mib,
                PageReadsPerSecond = 12,
                PagesInputPerSecond = 120
            };
            var feedback = new ReclaimFeedbackEvaluator().Evaluate(before, after, 768 * mib, 256 * mib,
                new GuardianOptions()) with
            {
                TargetIdentity = new ProcessIdentity(2468, 123456),
                TargetProcessName = "SampleApp"
            };
            for (var index = 0; index < 35; index++)
                store.RecordFeedback(feedback with { CapturedAt = now + TimeSpan.FromSeconds(index) });

            Assert.Null(store.Save());
            var restored = new LocalRuntimeStore(root);

            Assert.Equal(32, restored.RecentFeedbacks.Count);
            var last = restored.RecentFeedbacks[^1];
            Assert.Equal("SampleApp", last.TargetProcessName);
            Assert.Equal(1024 * mib, last.AvailableRamBeforeBytes);
            Assert.Equal(1280 * mib, last.AvailableRamAfterBytes);
            Assert.Equal(2048 * mib, last.SystemCommitBeforeBytes);
            Assert.Equal(768 * mib, last.TargetWorkingSetBeforeBytes);
            Assert.Equal(256 * mib, last.TargetWorkingSetAfterBytes);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimeStore_LoadsVersionOneStateWithoutRecoveryAudit()
    {
        var root = Path.Combine(Path.GetTempPath(), "MemGuardian.Next.Tests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "MemGuardian.Next");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "state.json"), "{\"Version\":1,\"State\":{},\"History\":[]}");

            var store = new LocalRuntimeStore(root);

            Assert.Empty(store.RecentFeedbacks);
            Assert.Empty(store.Warnings);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReclaimLogBuilder_SeparatesNativeCallsSkipsAndResidentOnlyFeedback()
    {
        const ulong mib = 1024 * 1024;
        var now = DateTimeOffset.UtcNow;
        var identity = new ProcessIdentity(1234, 5678);
        var before = new SystemMemoryMetrics(now, 8 * 1024 * mib, 512 * mib, 2048 * mib, 8 * 1024 * mib,
            512 * mib, 128 * mib, 256 * mib, 1, 20, 30, 4096 * mib);
        var after = before with
        {
            CapturedAt = now + TimeSpan.FromSeconds(5),
            AvailablePhysicalBytes = 768 * mib
        };
        var feedback = new ReclaimFeedbackEvaluator().Evaluate(before, after, 768 * mib, 256 * mib,
            new GuardianOptions()) with
        {
            CapturedAt = after.CapturedAt,
            TargetIdentity = identity,
            TargetProcessName = "SampleApp"
        };
        var round = new ReclaimRoundResult(new CandidateSelection(Array.Empty<ProcessSnapshot>(),
            Array.Empty<CandidateRejection>(), null), new[]
        {
            new ReclaimAttempt(identity, "SampleApp", true, null) { NativeCallAttempted = true },
            new ReclaimAttempt(new ProcessIdentity(1235, 5679), "DeniedApp", false, "Access denied") { NativeCallAttempted = true },
            new ReclaimAttempt(new ProcessIdentity(1236, 5680), "ExitedApp", false, "Process exited")
        }, new[] { feedback }, false);

        var entries = ReclaimLogBuilder.FromRound(round, ReclaimTrigger.Manual, now);

        Assert.Equal(3, entries.Count);
        Assert.Equal(ReclaimTrigger.Manual, entries[0].Trigger);
        Assert.Equal(ReclaimLogStatus.Succeeded, entries[0].Status);
        Assert.Equal(512 * mib, entries[0].WorkingSetReclaimedBytes);
        Assert.Equal(0, entries[0].CommitDeltaBytes);
        Assert.True(entries[0].ResidentPagesOnly);
        Assert.Contains("不代表释放了同等大小的 Commit", entries[0].Summary);
        Assert.Equal(ReclaimLogStatus.Failed, entries[1].Status);
        Assert.Equal(ReclaimLogStatus.Skipped, entries[2].Status);
    }

    [Fact]
    public void RuntimeStore_PersistsOnlyMostRecent128ReclaimLogEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "MemGuardian.Next.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRuntimeStore(root);
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < 140; index++)
            {
                store.RecordReclaimLog(ReclaimLogBuilder.Skipped(index % 2 == 0 ? ReclaimTrigger.Manual : ReclaimTrigger.Automatic,
                    now.AddSeconds(index), $"entry-{index}"));
            }

            Assert.Null(store.Save());
            var restored = new LocalRuntimeStore(root);

            Assert.Equal(128, restored.RecentReclaimLogs.Count);
            Assert.Equal("entry-12", restored.RecentReclaimLogs[0].Summary);
            Assert.Equal("entry-139", restored.RecentReclaimLogs[^1].Summary);
            Assert.Equal(ReclaimTrigger.Automatic, restored.RecentReclaimLogs[^1].Trigger);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimeStore_LoadsVersionTwoStateWithoutReclaimLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), "MemGuardian.Next.Tests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "MemGuardian.Next");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "state.json"), "{\"Version\":2,\"State\":{},\"History\":[],\"FeedbackHistory\":[]}");

            var store = new LocalRuntimeStore(root);

            Assert.Empty(store.RecentReclaimLogs);
            Assert.Empty(store.Warnings);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
