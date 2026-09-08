using System.Text.Json;
using ChizuChan.Commands;
using ChizuChan.Options;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static ChizuChan.LongMedia.Delivery.Tests.DurableDeliveryTests;

namespace ChizuChan.LongMedia.Delivery.Tests;

public class DeliveryPersistenceTests
{
    [Fact]
    public void Admission_reserves_journal_space_for_terminal_and_notification_records()
    {
        using var disk = new StoreDisk(maxBytes: 1024);
        var result = disk.Store.Admit(42, "abcdefghijk");
        Assert.Null(result.Job);
        Assert.Contains("storage", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(disk.Store.GetOwned(42));
    }

    [Fact]
    public void Reserved_record_space_handles_maximally_escaped_results_and_retry_bookkeeping()
    {
        using var disk = new StoreDisk(maxBytes: 4098);
        var job = disk.Store.Admit(ulong.MaxValue, "abcdefghijk").Job!;
        disk.Store.Finish(job.Id, YouTubeLongMediaState.Succeeded, new string('\uffff', 500));
        disk.Store.RecordDelivery(job.Id, null, DateTimeOffset.UtcNow);
        disk.Store.RecordDelivery(job.Id, ulong.MaxValue, DateTimeOffset.UtcNow);
        Assert.Equal(ulong.MaxValue, disk.Store.GetOwned(ulong.MaxValue)!.DeliveredMessageId);
    }

    [Fact]
    public async Task Fatal_journal_write_is_observed_and_stops_sibling_worker_loops()
    {
        await using var f = new Fixture();
        await f.Start();
        await f.Handler.HandleAsync(42, "abcdefghijk");
        await f.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Directory.CreateDirectory(Path.Combine(f.Root, "jobs.json.tmp"));
        f.Engine.Complete.SetResult(YouTubeMusicActionResult.Succeeded("Imported."));
        var hosted = (Microsoft.Extensions.Hosting.BackgroundService)f.Handler;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => hosted.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("Running", f.Jobs().Single()["State"]!.GetValue<string>());
        Assert.False((await f.Handler.HandleAsync(42, "ABCDEFGHI_0")).Success);
        Assert.Empty(f.Sends);
    }

    [Fact]
    public async Task Failed_DM_is_persisted_and_restart_retries_notification_without_redownloading()
    {
        var root = Path.Combine(Path.GetTempPath(), "chizu-delivery-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = new Fixture(root: root, keep: true))
            {
                first.FailDeliveries = true;
                first.Engine.Complete.SetResult(YouTubeMusicActionResult.Succeeded("Imported; Plex refresh pending."));
                await first.Start();
                await first.Handler.HandleAsync(42, "abcdefghijk");
                await first.Until(() => first.Jobs().Single()["DeliveryAttempts"]!.GetValue<int>() == 1);
                var job = first.Provider.GetRequiredService<YouTubeLongMediaStore>().GetOwned(42)!;
                Assert.Equal(YouTubeLongMediaState.Succeeded, job.State);
                Assert.Null(job.DeliveredMessageId);
                var status = YouTubeLongMediaStatusModule.FormatStatus(42, job.Id, first.Provider.GetRequiredService<YouTubeLongMediaStore>());
                Assert.Contains("retrying", status);
                Assert.DoesNotContain("DM delivered", status);
                await first.Stop();
                // Simulate restart after the retry delay, without waiting in real time.
                first.Provider.GetRequiredService<YouTubeLongMediaStore>().RecordDelivery(job.Id, null, DateTimeOffset.UtcNow.AddMinutes(-1));
            }
            await using var second = new Fixture(root: root, keep: true);
            await second.Start();
            await second.Until(() => second.Jobs().Single()["DeliveredMessageId"] is not null);
            Assert.Single(second.Sends);
            Assert.Equal(0, second.Engine.Calls);
            Assert.Contains("DM delivered", YouTubeLongMediaStatusModule.FormatStatus(42, null, second.Provider.GetRequiredService<YouTubeLongMediaStore>()));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Crash_running_record_becomes_interrupted_not_automatically_reexecuted()
    {
        var root = Path.Combine(Path.GetTempPath(), "chizu-delivery-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = new Fixture(root: root, keep: true))
            {
                await first.Handler.HandleAsync(42, "abcdefghijk");
                Assert.NotNull(first.Provider.GetRequiredService<YouTubeLongMediaStore>().ClaimNext());
                // No worker is running: leaving the persisted Running record models a hard crash.
            }
            await using var second = new Fixture(root: root, keep: true);
            await second.Start();
            await second.Until(() => second.Jobs().Single()["DeliveredMessageId"] is not null);
            Assert.Equal("Interrupted", second.Jobs().Single()["State"]!.GetValue<string>());
            Assert.Equal(0, second.Engine.Calls);
            Assert.Contains("may already have imported", second.Sends.Single().Content);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Delivered_success_is_not_redownloaded_or_renotified_on_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "chizu-delivery-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = new Fixture(root: root, keep: true))
            {
                first.Engine.Complete.SetResult(YouTubeMusicActionResult.Succeeded("Imported."));
                await first.Start();
                await first.Handler.HandleAsync(42, "abcdefghijk");
                await first.Until(() => first.Jobs().Single()["DeliveredMessageId"] is not null);
            }
            await using var second = new Fixture(root: root, keep: true);
            var store = second.Provider.GetRequiredService<YouTubeLongMediaStore>();
            Assert.Null(store.NextDelivery(DateTimeOffset.MaxValue));
            Assert.Null(store.ClaimNext());
            var repeated = await second.Handler.HandleAsync(42, "abcdefghijk");
            Assert.True(repeated.Success);
            Assert.Contains("already delivered", repeated.Message);
            Assert.DoesNotContain("I'll DM", repeated.Message);
            Assert.Single(second.Jobs());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Journal_lock_prevents_overlapping_hosts()
    {
        using var disk = new StoreDisk();
        Assert.Throws<IOException>(() => new YouTubeLongMediaStore(Microsoft.Extensions.Options.Options.Create(disk.Options)));
    }

    [Fact]
    public void Corrupt_journal_fails_closed_without_erasing_existing_records()
    {
        var root = Path.Combine(Path.GetTempPath(), "chizu-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "jobs.json");
        try
        {
            File.WriteAllText(path, "not valid JSON");
            Assert.Throws<JsonException>(() => new YouTubeLongMediaStore(Microsoft.Extensions.Options.Options.Create(new YouTubeLongMediaDeliveryOptions { StorePath = path })));
            Assert.Equal("not valid JSON", File.ReadAllText(path));
            // Failed startup released its lease, so an operator can repair it and restart.
            File.WriteAllText(path, "[]");
            using var recovered = new YouTubeLongMediaStore(Microsoft.Extensions.Options.Options.Create(new YouTubeLongMediaDeliveryOptions { StorePath = path }));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Full_history_is_honest_resource_denial_and_existing_status_remains_available()
    {
        using var disk = new StoreDisk(maxStored: 1);
        var job = disk.Store.Admit(42, "abcdefghijk").Job!;
        disk.Store.Finish(job.Id, YouTubeLongMediaState.Succeeded, "Imported.");
        var denied = disk.Store.Admit(42, "ABCDEFGHI_0");
        Assert.Null(denied.Job);
        Assert.Contains("storage", denied.Error!);
        Assert.Equal(YouTubeLongMediaState.Succeeded, disk.Store.GetOwned(42, job.Id)!.State);
    }

    [Fact]
    public async Task Different_users_have_independent_owned_status_and_no_job_identity_leak()
    {
        await using var f = new Fixture();
        var first = await f.Handler.HandleAsync(42, "abcdefghijk");
        var second = await f.Handler.HandleAsync(99, "abcdefghijk");
        Assert.NotEqual(first.Message, second.Message);
        var store = f.Provider.GetRequiredService<YouTubeLongMediaStore>();
        Assert.Null(store.GetOwned(99, store.GetOwned(42)!.Id));
        Assert.Equal(2, f.Jobs().Length);
    }

    [Fact]
    public async Task Admission_failure_does_not_report_queued_or_start_engine()
    {
        await using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Root, "jobs.json.tmp"));
        var result = await f.Handler.HandleAsync(42, "abcdefghijk");
        Assert.False(result.Success);
        Assert.Contains("storage", result.Message);
        Assert.Empty(f.Jobs());
        Assert.Equal(0, f.Engine.Calls);
    }

    [Fact]
    public async Task Single_worker_bounds_concurrency_without_timing_out_waiting_jobs()
    {
        await using var f = new Fixture();
        await f.Start();
        await f.Handler.HandleAsync(42, "abcdefghijk");
        await f.Handler.HandleAsync(42, "ABCDEFGHI_0");
        await f.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, f.Engine.Calls);
        Assert.Single(f.Jobs(), j => j["State"]!.GetValue<string>() == "Queued");
        f.Engine.Complete.SetResult(YouTubeMusicActionResult.Succeeded("Imported."));
        await f.Until(() => f.Sends.Count == 2);
        Assert.Equal(2, f.Engine.Calls);
    }

    internal sealed class StoreDisk : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "chizu-delivery-" + Guid.NewGuid().ToString("N"));
        public readonly YouTubeLongMediaStore Store;
        public readonly YouTubeLongMediaDeliveryOptions Options;
        public StoreDisk(int maxBytes = 16 * 1024 * 1024, int maxStored = 1000)
        {
            Options = new YouTubeLongMediaDeliveryOptions { StorePath = Path.Combine(_root, "jobs.json"), MaxStoreBytes = maxBytes, MaxStoredJobs = maxStored, MaxPendingJobs = 1 };
            Store = new YouTubeLongMediaStore(Microsoft.Extensions.Options.Options.Create(Options));
        }
        public void Dispose() { Store.Dispose(); Directory.Delete(_root, true); }
    }
}
