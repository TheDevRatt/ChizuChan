using System.Text.Json;
using ChizuChan.Options;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChizuChan.LongMedia.Engine.Tests;

public sealed class AudioTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "chizu-engine-audio-" + Guid.NewGuid());
    private string Audio => Path.Combine(_directory, "source.m4a");
    private string Cover => Path.Combine(_directory, "cover.jpg");
    private static string Ffmpeg => Environment.GetEnvironmentVariable("CHIZU_TEST_FFMPEG") ?? "ffmpeg";
    private static string Ffprobe => Environment.GetEnvironmentVariable("CHIZU_TEST_FFPROBE") ?? "ffprobe";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await Run(Ffmpeg, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:a", "aac", "-b:a", "64k", Audio]);
        await Run(Ffmpeg, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=red:size=32x32", "-frames:v", "1", "-threads", "1", Cover]);
    }
    public Task DisposeAsync() { Directory.Delete(_directory, true); return Task.CompletedTask; }

    private async Task<string> Run(string exe, IReadOnlyList<string> arguments)
    {
        var result = await new YouTubeDownloadTool().RunAsync(new(exe, arguments, _directory, TimeSpan.FromSeconds(20), 256 * 1024, 8192), default);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput;
    }
    private async Task<string> AudioPacketHashes(string path) => await Run(Ffprobe,
        ["-v", "error", "-select_streams", "a:0", "-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=data_hash", "-of", "csv=p=0", path]);

    [Fact]
    public async Task TaggingPreservesEncodedPacketsCoverTagsAndDuration()
    {
        var output = Path.Combine(_directory, "tagged.m4a");
        var metadata = new YouTubeTrackMetadata("My title", "My artist", "My album", "YouTube", 2026, "2026", 3);
        var args = YouTubeMusicActionHandler.BuildFfmpegArguments(Audio, Cover, output, metadata, "https://www.youtube.com/watch?v="+PolicyTests.Id);
        await Run(Ffmpeg, args);
        Assert.Equal(await AudioPacketHashes(Audio), await AudioPacketHashes(output));
        using var info = JsonDocument.Parse(await Run(Ffprobe, ["-v", "error", "-show_format", "-show_streams", "-of", "json", output]));
        var format = info.RootElement.GetProperty("format");
        Assert.InRange(double.Parse(format.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 2.95, 3.1);
        Assert.Equal("My title", format.GetProperty("tags").GetProperty("title").GetString());
        Assert.Equal("My artist", format.GetProperty("tags").GetProperty("artist").GetString());
        Assert.Equal("My album", format.GetProperty("tags").GetProperty("album").GetString());
        Assert.Contains(info.RootElement.GetProperty("streams").EnumerateArray(), s => s.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 1);
    }

    [Fact]
    public async Task TaggingWithoutReplacementCoverPreservesExistingCover()
    {
        var withCover = Path.Combine(_directory, "with-cover.m4a");
        var retagged = Path.Combine(_directory, "retagged.m4a");
        var metadata = new YouTubeTrackMetadata("Title", "Artist", "Album", "YouTube", null, null, 3);
        await Run(Ffmpeg, YouTubeMusicActionHandler.BuildFfmpegArguments(Audio, Cover, withCover, metadata, "url"));
        await Run(Ffmpeg, YouTubeMusicActionHandler.BuildFfmpegArguments(withCover, null, retagged, metadata, "url"));
        using var info = JsonDocument.Parse(await Run(Ffprobe, ["-v", "error", "-show_streams", "-of", "json", retagged]));
        Assert.Contains(info.RootElement.GetProperty("streams").EnumerateArray(), s => s.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 1);
    }

    [Fact]
    public async Task CancellationAfterVerificationCannotPromoteMedia()
    {
        var root = Path.Combine(_directory, "library");
        using var cancellation = new CancellationTokenSource();
        var inner = new AcquisitionFixture(Audio, Cover, 3);
        var tool = new CancelAfterVerificationTool(inner, cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(tool, Settings(root)).HandleAsync(1, PolicyTests.Id, cancellation.Token));
        Assert.Empty(Directory.GetFiles(root, "*.m4a", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(root, ".chizu-staging")));
    }

    [Fact]
    public async Task ExplicitNewFileSizeDenialNamesPolicyNotStorage()
    {
        var root = Path.Combine(_directory, "library");
        var options = Settings(root);
        options.MaxFileSizeBytes = 16;
        var result = await Handler(new AcquisitionFixture(Audio, Cover, 3), options).HandleAsync(1, PolicyTests.Id);
        Assert.False(result.Success);
        Assert.Contains("file size limit", result.Message);
        Assert.Empty(Directory.GetFiles(root, "*.m4a", SearchOption.AllDirectories));
    }

    private sealed class CancelAfterVerificationTool(IYouTubeDownloadTool inner, CancellationTokenSource cancel) : IYouTubeDownloadTool
    {
        public async Task<YouTubeDownloadToolResult> RunAsync(YouTubeDownloadToolInvocation invocation, CancellationToken token)
        {
            var result = await inner.RunAsync(invocation, token);
            if (invocation.Arguments.Contains("null")) cancel.Cancel();
            return result;
        }
    }

    [Fact]
    public async Task CorruptionNearTheRealTailIsRejected()
    {
        var damaged = Path.Combine(_directory, "damaged.m4a");
        await Run(Ffmpeg, ["-v", "error", "-i", Audio, "-c", "copy", "-movflags", "+faststart", damaged]);
        await using (var stream = new FileStream(damaged, FileMode.Open, FileAccess.Write))
        {
            stream.Position = stream.Length - 4096;
            await stream.WriteAsync(new byte[4096]);
        }
        var result = await new YouTubeDownloadTool().RunAsync(new(Ffmpeg, YouTubeMusicActionHandler.BuildAudioVerificationArguments(damaged), _directory, TimeSpan.FromSeconds(20), 8192, 8192)
            { OutputMode = YouTubeDownloadOutputMode.Diagnostics }, default);
        Assert.True(result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.StandardError));
    }

    [Fact]
    public async Task SameVideoWaiterCancellationDoesNotLeakLocksOrStaging()
    {
        var root = Path.Combine(_directory, "library");
        var tool = new BlockFirstProbeTool(new AcquisitionFixture(Audio, Cover, 3));
        var handler = Handler(tool, Settings(root));
        using var firstCancellation = new CancellationTokenSource();
        var first = handler.HandleAsync(1, PolicyTests.Id, firstCancellation.Token);
        await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var waiterCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync(2, PolicyTests.Id, waiterCancellation.Token));
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(Directory.Exists(Path.Combine(root, ".chizu-staging")));
        Assert.True((await handler.HandleAsync(1, PolicyTests.Id)).Success);
    }

    private sealed class BlockFirstProbeTool(IYouTubeDownloadTool inner) : IYouTubeDownloadTool
    {
        private int _calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<YouTubeDownloadToolResult> RunAsync(YouTubeDownloadToolInvocation invocation, CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return await inner.RunAsync(invocation, token);
        }
    }

    [Fact]
    public async Task VerificationDecodesThroughTheRealTail()
    {
        var result = await Run(Ffmpeg, YouTubeMusicActionHandler.BuildAudioVerificationArguments(Audio));
        Assert.Contains("progress=end", result);
        Assert.Contains("out_time_us=", result);
        var last = result.Split('\n').Last(l => l.StartsWith("out_time_us="));
        Assert.True(long.Parse(last.Split('=')[1]) >= 2900000);
    }

    private YouTubeMusicDownloadOptions Settings(string root) => new() { Enabled = true, LibraryRootPath = root, FfmpegPath = ResolveTool(Ffmpeg) };
    private static string ResolveTool(string path)
    {
        if (Path.IsPathFullyQualified(path)) return path;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? path + ".exe" : path);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Required test media tool unavailable", path);
    }
    private static YouTubeMusicActionHandler Handler(IYouTubeDownloadTool tool, YouTubeMusicDownloadOptions options) => new(tool, Microsoft.Extensions.Options.Options.Create(options), NullLogger<YouTubeMusicActionHandler>.Instance);

    [Fact]
    public async Task RealPipelinePromotesThenReusesFileWithCapsDisabled()
    {
        var root = Path.Combine(_directory, "library");
        var tool = new AcquisitionFixture(Audio, Cover, 3);
        var options = Settings(root);
        var handler = Handler(tool, options);
        var result = await handler.HandleAsync(1, PolicyTests.Id);
        Assert.True(result.Success, result.Message);
        var media = Directory.GetFiles(root, "*.m4a", SearchOption.AllDirectories);
        Assert.Single(media);
        Assert.Equal(await AudioPacketHashes(Audio), await AudioPacketHashes(media[0]));
        Assert.True((await handler.HandleAsync(1, PolicyTests.Id)).Success);
        Assert.Equal(1, tool.Acquisitions);
        Assert.False(Directory.Exists(Path.Combine(root, ".chizu-staging")));
        Assert.Contains(tool.Invocations, i => i.Arguments.Contains("--print") && i.OutputMode == YouTubeDownloadOutputMode.Metadata);
        Assert.All(tool.Invocations.Where(i => !i.Arguments.Contains("--print")), i => Assert.Equal(YouTubeDownloadOutputMode.Diagnostics, i.OutputMode));
        var acquire = Assert.Single(tool.Invocations, i => i.Arguments.Contains("--extract-audio"));
        Assert.True(acquire.MinimumFreeSpaceBytes > 0);
        Assert.True(acquire.StalledWorkTimeout > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, acquire.Timeout);
    }

    [Fact]
    public async Task LoweredSizePolicyNeverQuarantinesValidIndexedMedia()
    {
        var root = Path.Combine(_directory, "library");
        var media = Path.Combine(root, "Artist", $"Track [{PolicyTests.Id}].m4a");
        Directory.CreateDirectory(Path.GetDirectoryName(media)!);
        File.Copy(Audio, media);
        var index = Path.Combine(root, ".chizu-index", PolicyTests.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new { version = 1, videoId = PolicyTests.Id, relativePath = Path.GetRelativePath(root, media) }));
        var options = Settings(root);
        options.MaxFileSizeBytes = 16;
        var tool = new AcquisitionFixture(Audio, Cover, 3);
        var result = await Handler(tool, options).HandleAsync(1, PolicyTests.Id);
        Assert.True(result.Success, result.Message);
        Assert.Contains("already", result.Message);
        Assert.Equal(0, tool.Acquisitions);
        Assert.True(File.Exists(media));
        Assert.False(Directory.Exists(Path.Combine(root, ".chizu-quarantine")));
    }

    [Fact]
    public async Task ShortRealAudioCannotMasqueradeAsSevenHourDownload()
    {
        var root = Path.Combine(_directory, "library");
        var options = Settings(root);
        options.MaxFileSizeBytes = 10000000; // Reach verification even against the pre-fix zero-cap bug.
        var result = await Handler(new AcquisitionFixture(Audio, Cover, 25200), options).HandleAsync(1, PolicyTests.Id);
        Assert.False(result.Success);
        Assert.Contains("duration", result.Message);
        Assert.Empty(Directory.GetFiles(root, "*.m4a", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(root, ".chizu-staging")));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ResourceFailuresNameTheCauseAndCleanStaging(bool lowSpace)
    {
        var root = Path.Combine(_directory, "library");
        var result = await Handler(new FailingTool(lowSpace), Settings(root)).HandleAsync(1, PolicyTests.Id);
        Assert.False(result.Success);
        Assert.Contains(lowSpace ? "free space" : "stalled", result.Message);
        Assert.False(Directory.Exists(Path.Combine(root, ".chizu-staging")));
    }

    private sealed class FailingTool(bool lowSpace) : IYouTubeDownloadTool
    {
        public Task<YouTubeDownloadToolResult> RunAsync(YouTubeDownloadToolInvocation invocation, CancellationToken token) =>
            throw (lowSpace ? (Exception)new YouTubeLongMediaLowSpaceException() : new YouTubeLongMediaStalledException());
    }

    // Only acquisition is substituted. Every audio generation, tagging and verification is real ffmpeg.
    private sealed class AcquisitionFixture(string audio, string cover, double duration) : IYouTubeDownloadTool
    {
        public int Acquisitions { get; private set; }
        public List<YouTubeDownloadToolInvocation> Invocations { get; } = [];
        public async Task<YouTubeDownloadToolResult> RunAsync(YouTubeDownloadToolInvocation invocation, CancellationToken token)
        {
            Invocations.Add(invocation);
            if (invocation.Arguments.Contains("--print")) return new(0, PolicyTests.Metadata(duration), "");
            if (invocation.Arguments.Contains("--extract-audio"))
            {
                Acquisitions++;
                File.Copy(audio, Path.Combine(invocation.WorkingDirectory, "download.m4a"));
                File.Copy(cover, Path.Combine(invocation.WorkingDirectory, "download.jpg"));
                return new(0, "", "");
            }
            return await new YouTubeDownloadTool().RunAsync(invocation, token);
        }
    }
}
