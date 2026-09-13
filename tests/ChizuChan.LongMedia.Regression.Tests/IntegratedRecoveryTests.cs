using ChizuChan.Options;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChizuChan.LongMedia.Regression.Tests;

public sealed class IntegratedRecoveryTests
{
    [Fact]
    public async Task Durable_retry_rechecks_existing_audio_then_reimports_after_external_deletion()
    {
        using var fixture = await RealAudioFixture.Create();
        var options = Microsoft.Extensions.Options.Options.Create(new YouTubeLongMediaDeliveryOptions
        {
            StorePath = Path.Combine(fixture.Scratch, "jobs.json"), PollIntervalMilliseconds = 10,
        });
        using var store = new YouTubeLongMediaStore(options);
        using var delivery = new YouTubeLongMediaDelivery(fixture.Handler, store, new Messenger(), options,
            NullLogger<YouTubeLongMediaDelivery>.Instance);
        await delivery.StartAsync(default);
        try
        {
            async Task<YouTubeLongMediaJob> Submit()
            {
                Assert.True((await delivery.HandleAsync(42, Samples.VideoId)).Success);
                var job = store.GetOwned(42)!;
                using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (store.GetOwned(42, job.Id)!.DeliveredMessageId is null) await Task.Delay(10, safety.Token);
                var done = store.GetOwned(42, job.Id)!;
                Assert.Equal(YouTubeLongMediaState.Succeeded, done.State);
                return done;
            }
            var first = await Submit();
            var file = Assert.Single(Directory.GetFiles(fixture.Root, "*.m4a", SearchOption.AllDirectories));
            var before = await File.ReadAllBytesAsync(file);
            var second = await Submit();
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(1, fixture.Tool.DownloadCount);
            Assert.Equal(before, await File.ReadAllBytesAsync(file));
            File.Delete(file); // Intentional user/library cleanup, not an application failure.
            var third = await Submit();
            Assert.NotEqual(second.Id, third.Id);
            Assert.True(File.Exists(file));
            Assert.Equal(2, fixture.Tool.DownloadCount);
        }
        finally { await delivery.StopAsync(default); }
    }

    private sealed class Messenger : IYouTubeLongMediaMessenger
    {
        private long _id;
        public Task<ulong> SendAsync(YouTubeLongMediaJob job, CancellationToken cancellationToken) =>
            Task.FromResult((ulong)Interlocked.Increment(ref _id));
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("exit")]
    [InlineData("locked-media")]
    [InlineData("locked-index")]
    public async Task Verification_IO_failure_never_quarantines_valid_import(string failure)
    {
        using var fixture = await RealAudioFixture.Create();
        Assert.True((await fixture.Handler.HandleAsync(42, Samples.VideoId)).Success);
        var file = Assert.Single(Directory.GetFiles(fixture.Root, "*.m4a", SearchOption.AllDirectories));
        var index = Path.Combine(fixture.Root, ".chizu-index", Samples.VideoId + ".json");
        var mediaBefore = await File.ReadAllBytesAsync(file);
        var indexBefore = await File.ReadAllBytesAsync(index);
        var tool = new VerificationFailureTool(fixture.Tool, failure);
        var handler = new YouTubeMusicActionHandler(tool, Microsoft.Extensions.Options.Options.Create(fixture.Options),
            NullLogger<YouTubeMusicActionHandler>.Instance);
        using (var locked = failure.StartsWith("locked")
            ? new FileStream(failure == "locked-media" ? file : index, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
        {
            var result = await handler.HandleAsync(42, Samples.VideoId);
            Assert.False(result.Success);
            Assert.Contains("I/O", result.Message);
            Assert.True(File.Exists(file));
            Assert.True(File.Exists(index));
        }
        Assert.Equal(mediaBefore, await File.ReadAllBytesAsync(file));
        Assert.Equal(indexBefore, await File.ReadAllBytesAsync(index));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.invalid-*", SearchOption.AllDirectories));
        Assert.Equal(1, fixture.Tool.DownloadCount);
    }

    private sealed class VerificationFailureTool(IYouTubeDownloadTool inner, string failure) : IYouTubeDownloadTool
    {
        public Task<YouTubeDownloadToolResult> RunAsync(YouTubeDownloadToolInvocation invocation, CancellationToken token)
        {
            if (invocation.Arguments.Contains("null"))
            {
                if (failure == "exception") throw new IOException("Synthetic OS read failure, not corrupt media");
                if (failure == "exit") return Task.FromResult(new YouTubeDownloadToolResult(1, "", "Input/output error"));
            }
            return inner.RunAsync(invocation, token);
        }
    }
}
