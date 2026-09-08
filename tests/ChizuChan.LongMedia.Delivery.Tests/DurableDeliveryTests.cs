using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ChizuChan;
using ChizuChan.Commands;
using ChizuChan.Extensions;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ChizuChan.LongMedia.Delivery.Tests;

public class DurableDeliveryTests
{
    [Fact]
    public async Task Direct_acknowledges_queued_job_before_engine_finishes_and_survives_expiry()
    {
        await using var f = new Fixture();
        await f.Start();
        var interaction = new DeliveryEntryPointTests.ExpiringInteraction();
        await interaction.Acknowledge(default);
        var result = await YouTubeDownloadCommandCoordinator.ExecuteAsync(42,
            "https://youtu.be/abcdefghijk", f.Access.Object, f.Handler, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(2));
        await interaction.Commit(new MusicSearchActionResult(result.Success, result.Message), default);
        Assert.True(result.Success);
        Assert.Contains("job", result.Message);
        Assert.Contains("/youtube_job", result.Message);
        await f.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        interaction.Advance(TimeSpan.FromMinutes(16));
        await Assert.ThrowsAsync<IOException>(() => interaction.Commit(default, default));
        f.Engine.Complete.TrySetResult(YouTubeMusicActionResult.Succeeded("Imported; Plex refresh pending."));
        await f.Until(() => f.Sends.Count == 1);
        Assert.Equal(42ul, f.Sends.Single().User);
        Assert.Contains("Plex refresh pending", f.Sends.Single().Content);
        Assert.Equal("Succeeded", f.Jobs().Single()["State"]!.GetValue<string>());
    }

    [Fact]
    public async Task Admission_is_persisted_and_duplicate_submissions_share_job()
    {
        await using var f = new Fixture();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => f.Handler.HandleAsync(42, "abcdefghijk")));
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Single(f.Jobs());
        Assert.Single(results.Select(r => r.Message).Distinct());
        Assert.Equal(0, f.Engine.Calls);
    }

    [Fact]
    public async Task Queue_capacity_reports_busy_without_losing_accepted_work()
    {
        await using var f = new Fixture(maxPending: 1);
        Assert.True((await f.Handler.HandleAsync(42, "abcdefghijk")).Success);
        var busy = await f.Handler.HandleAsync(42, "ABCDEFGHI_0");
        Assert.False(busy.Success);
        Assert.Contains("busy", busy.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(f.Jobs());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unauthorized_or_rate_limited_direct_command_never_enqueues(bool rateLimit)
    {
        await using var f = new Fixture();
        f.Access.Setup(x => x.CheckAccess(42, MusicRequestOperation.Download)).Returns(rateLimit
            ? MusicRequestAccessResult.RateLimited(TimeSpan.FromSeconds(3)) : MusicRequestAccessResult.Unauthorized());
        var result = await YouTubeDownloadCommandCoordinator.ExecuteAsync(42,
            "https://youtu.be/abcdefghijk", f.Access.Object, f.Handler, NullLogger.Instance);
        Assert.False(result.Success);
        Assert.Empty(f.Jobs());
    }

    [Fact]
    public async Task Queued_work_resumes_after_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "chizu-delivery-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = new Fixture(root: root, keep: true))
                Assert.True((await first.Handler.HandleAsync(42, "abcdefghijk")).Success);
            await using var second = new Fixture(root: root, keep: true);
            second.Engine.Complete.TrySetResult(YouTubeMusicActionResult.Succeeded("Imported."));
            await second.Start();
            await second.Until(() => second.Sends.Count == 1);
            Assert.Equal(1, second.Engine.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Shutdown_cancels_engine_and_persists_interrupted_state_for_notification_after_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "chizu-delivery-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = new Fixture(root: root, keep: true))
            {
                await first.Start();
                await first.Handler.HandleAsync(42, "abcdefghijk");
                await first.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await first.Stop();
                Assert.True(first.Engine.Canceled);
                Assert.Equal("Interrupted", first.Jobs().Single()["State"]!.GetValue<string>());
            }
            await using var second = new Fixture(root: root, keep: true);
            await second.Start();
            await second.Until(() => second.Sends.Count == 1);
            Assert.Equal(0, second.Engine.Calls);
            Assert.Contains("interrupted", second.Sends.Single().Content, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handler_failure_or_exception_is_terminal_and_delivered_honestly(bool throws)
    {
        await using var f = new Fixture();
        if (throws) f.Engine.Complete.TrySetException(new InvalidOperationException("secret path"));
        else f.Engine.Complete.TrySetResult(YouTubeMusicActionResult.Failed("Disk reserve unavailable."));
        await f.Start();
        await f.Handler.HandleAsync(42, "abcdefghijk");
        await f.Until(() => f.Sends.Count == 1);
        Assert.Equal("Failed", f.Jobs().Single()["State"]!.GetValue<string>());
        Assert.DoesNotContain("secret path", f.Sends.Single().Content);
        if (!throws) Assert.Contains("Disk reserve unavailable", f.Sends.Single().Content);
    }

    [Fact]
    public async Task Canceled_admission_writes_nothing()
    {
        await using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Handler.HandleAsync(42, "abcdefghijk", cts.Token));
        Assert.Empty(f.Jobs());
    }

    internal sealed class Engine : IYouTubeMusicActionHandler
    {
        public int Calls;
        public bool Canceled;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<YouTubeMusicActionResult> Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<YouTubeMusicActionResult> HandleAsync(ulong userId, string canonicalVideoId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            try { return await Complete.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { Canceled = true; throw; }
        }
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public readonly string Root;
        public readonly Engine Engine = new();
        public readonly Mock<IMusicRequestAccessService> Access = new();
        public readonly ConcurrentQueue<(ulong User, string Content)> Sends = new();
        public bool FailDeliveries;
        public readonly ServiceProvider Provider;
        public IYouTubeMusicActionHandler Handler => Provider.GetRequiredService<IYouTubeMusicActionHandler>();
        private readonly bool _keep;
        private bool _started;
        public Fixture(int maxPending = 16, string? root = null, bool keep = false)
        {
            Root = root ?? Path.Combine(Path.GetTempPath(), "chizu-delivery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            _keep = keep;
            Access.Setup(x => x.CheckAccess(It.IsAny<ulong>(), It.IsAny<MusicRequestOperation>())).Returns(MusicRequestAccessResult.Allowed());
            var services = new ServiceCollection().AddLogging();
            services.AddAllServicesFromAssembly(typeof(Program).Assembly);
            services.AddSingleton(Access.Object);
            services.AddSingleton(Mock.Of<ILidarrService>(MockBehavior.Strict));
            services.AddSingleton(Mock.Of<ISoulseekTrackSearchService>(MockBehavior.Strict));
            services.AddSingleton(Mock.Of<IMusicRequestNotificationStore>(MockBehavior.Strict));
            services.AddSingleton<IYouTubeMusicActionHandler>(Engine);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["YouTubeLongMediaDelivery:StorePath"] = Path.Combine(Root, "jobs.json"),
                ["YouTubeLongMediaDelivery:MaxPendingJobs"] = maxPending.ToString(),
                ["YouTubeLongMediaDelivery:PollIntervalMilliseconds"] = "10",
            }).Build();
            var registration = typeof(Program).GetMethod("AddYouTubeLongMediaDelivery");
            Assert.NotNull(registration);
            registration.Invoke(null, [services, config]);
            // Override only the transport at the network boundary. All job/store/worker code is real.
            ConfigureSender(services);
            Provider = services.BuildServiceProvider();
            Assert.NotSame(Engine, Handler);
        }
        private void ConfigureSender(IServiceCollection services) =>
            services.AddSingleton<IYouTubeLongMediaMessenger>(new RecordingMessenger(this));
        private sealed class RecordingMessenger(Fixture fixture) : IYouTubeLongMediaMessenger
        {
            public Task<ulong> SendAsync(YouTubeLongMediaJob job, CancellationToken cancellationToken)
            {
                if (fixture.FailDeliveries) throw new IOException("Discord DMs blocked");
                var message = YouTubeLongMediaMessenger.BuildMessage(job);
                Assert.NotNull(message.AllowedMentions);
                Assert.False(message.AllowedMentions.Everyone);
                Assert.False(message.AllowedMentions.ReplyMention);
                Assert.Empty(message.AllowedMentions.AllowedUsers!);
                Assert.Empty(message.AllowedMentions.AllowedRoles!);
                fixture.Sends.Enqueue((job.OwnerUserId, message.Content!));
                return Task.FromResult(123ul);
            }
        }
        public JsonObject[] Jobs() => !File.Exists(Path.Combine(Root, "jobs.json")) ? [] :
            JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "jobs.json")))!.AsArray().Select(x => x!.AsObject()).ToArray();
        public async Task Start() { foreach (var worker in Provider.GetServices<IHostedService>()) await worker.StartAsync(default); _started = true; }
        public async Task Stop() { if (!_started) return; foreach (var worker in Provider.GetServices<IHostedService>()) await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3)); _started = false; }
        public async Task Until(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition()) await Task.Delay(10, deadline.Token);
        }
        public async ValueTask DisposeAsync()
        {
            await Stop();
            await Provider.DisposeAsync();
            if (!_keep) Directory.Delete(Root, true);
        }
    }
}
