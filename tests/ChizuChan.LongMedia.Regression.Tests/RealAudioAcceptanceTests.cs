using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ChizuChan.Options;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChizuChan.LongMedia.Regression.Tests;

[Trait("Category", "RealAudio")]
public sealed class RealAudioAcceptanceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Generated_audio_survives_real_tagging_with_duration_tail_tags_and_cover_intact()
    {
        using var fixture = await RealAudioFixture.Create();
        var result = await fixture.Handler.HandleAsync(42, Samples.VideoId);
        Assert.True(result.Success, result.Message);
        var final = Assert.Single(Directory.GetFiles(fixture.Root, "*.m4a", SearchOption.AllDirectories));
        using var measured = await fixture.Probe(final);
        var format = measured.RootElement.GetProperty("format");
        var duration = double.Parse(format.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(duration, 2.45, 2.60);
        var tags = format.GetProperty("tags");
        Assert.Equal("Acceptance tone", tags.GetProperty("title").GetString());
        Assert.Equal("Local generator", tags.GetProperty("artist").GetString());
        Assert.Equal("Offline fixtures", tags.GetProperty("album").GetString());
        Assert.Equal("1/1", tags.GetProperty("track").GetString());
        Assert.Equal("Source: " + Samples.Url, tags.GetProperty("comment").GetString());
        Assert.Contains(measured.RootElement.GetProperty("streams").EnumerateArray(),
            stream => stream.GetProperty("codec_type").GetString() == "video" &&
                      stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 1);

        // Decode the WHOLE file independently, not only production's one-second verification.
        var pcm = Path.Combine(fixture.Scratch, "decoded.s16le");
        await LocalProcess.Run(Samples.ToolPath("ffmpeg"),
            ["-nostdin", "-v", "error", "-xerror", "-i", final, "-map", "0:a:0", "-ac", "1", "-ar", "8000", "-f", "s16le", pcm], fixture.Scratch);
        var samples = await File.ReadAllBytesAsync(pcm);
        Assert.InRange(samples.Length, 39200, 42000);
        var tail = Enumerable.Range(samples.Length / 2 - 2000, 2000)
            .Select(i => (double)BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(i * 2, 2))).ToArray();
        var tailRms = Math.Sqrt(tail.Average(value => value * value));
        output.WriteLine($"Real generated source: 2.5s; ffprobe tagged duration: {duration.ToString(CultureInfo.InvariantCulture)}s; decoded PCM: {samples.Length} bytes; tail RMS: {tailRms.ToString("F2", CultureInfo.InvariantCulture)}; cover and authoritative tags verified.");
        Assert.True(tailRms > 100,
            "Last quarter-second of generated tone was lost or replaced by silence.");
    }

    [Fact]
    public async Task Successful_tool_exit_does_not_allow_grossly_truncated_real_audio_to_be_promoted()
    {
        using var fixture = await RealAudioFixture.Create();
        fixture.Tool.ReportedDuration = 180; // Real payload is only 2.5 seconds. Not a sparse fixture.
        var result = await fixture.Handler.HandleAsync(42, Samples.VideoId);
        Assert.False(result.Success, "A 2.5s real audio payload was published for 180s metadata despite successful tool exits.");
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.m4a", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Lower_acquisition_size_budget_does_not_quarantine_valid_existing_library_audio()
    {
        using var fixture = await RealAudioFixture.Create();
        Assert.True((await fixture.Handler.HandleAsync(42, Samples.VideoId)).Success);
        var final = Assert.Single(Directory.GetFiles(fixture.Root, "*.m4a", SearchOption.AllDirectories));
        // Add a valid ISO-BMFF free box. These are REAL written bytes, and the audio remains real.
        // This tests a lower size policy without generating multi-hour audio or sparse pseudo-audio.
        var free = new byte[2 * 1024 * 1024];
        BinaryPrimitives.WriteUInt32BigEndian(free, (uint)free.Length);
        "free"u8.CopyTo(free.AsSpan(4));
        await using (var stream = new FileStream(final, FileMode.Append, FileAccess.Write)) await stream.WriteAsync(free);
        await LocalProcess.Run(Samples.ToolPath("ffmpeg"),
            ["-nostdin", "-v", "error", "-xerror", "-i", final, "-map", "0:a:0", "-f", "null", OperatingSystem.IsWindows() ? "NUL" : "/dev/null"], fixture.Scratch);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(final));
        fixture.Options.MaxFileSizeBytes = 1024 * 1024;
        fixture.Tool.RefuseAcquisition = true;
        var result = await fixture.Handler.HandleAsync(42, Samples.VideoId);
        Assert.True(File.Exists(final), "Valid existing real audio was moved to quarantine solely because the acquisition size budget was lowered.");
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(final)));
        Assert.True(result.Success, result.Message);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.invalid-*", SearchOption.AllDirectories));
        Assert.Equal(1, fixture.Tool.DownloadCount);
    }
}

internal sealed class RealAudioFixture : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    public string Scratch => _scratch.Path;
    public string Root { get; }
    public RealAcquisitionTool Tool { get; }
    public YouTubeMusicDownloadOptions Options { get; }
    public YouTubeMusicActionHandler Handler { get; }

    private RealAudioFixture()
    {
        Root = Path.Combine(Scratch, "library");
        var source = Path.Combine(Scratch, "source.m4a");
        Tool = new RealAcquisitionTool(source, Path.Combine(Scratch, "cover.jpg"));
        Options = new YouTubeMusicDownloadOptions
        {
            Enabled = true, LibraryRootPath = Root, YtDlpPath = source, FfmpegPath = Samples.ToolPath("ffmpeg"),
        };
        Handler = new YouTubeMusicActionHandler(Tool, Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<YouTubeMusicActionHandler>.Instance);
    }

    public static async Task<RealAudioFixture> Create()
    {
        var fixture = new RealAudioFixture();
        try
        {
            await LocalProcess.Run(Samples.ToolPath("ffmpeg"),
                ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100:duration=2.5",
                 "-c:a", "aac", "-b:a", "128k", Path.Combine(fixture.Scratch, "source.m4a")], fixture.Scratch);
            await LocalProcess.Run(Samples.ToolPath("ffmpeg"),
                ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=32x32",
                 "-frames:v", "1", "-threads", "1", Path.Combine(fixture.Scratch, "cover.jpg")], fixture.Scratch);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    public async Task<JsonDocument> Probe(string file)
    {
        var result = await LocalProcess.Run(Samples.ToolPath("ffprobe"),
            ["-v", "error", "-show_format", "-show_streams", "-of", "json", file], Scratch);
        return JsonDocument.Parse(result.Output);
    }
    public void Dispose() => _scratch.Dispose();
}

// No network acquisition. Metadata and download are synthetic; every tag/verify invocation
// is actually executed by the production runner against the generated, decodable audio.
internal sealed class RealAcquisitionTool(string source, string cover) : IYouTubeDownloadTool
{
    private readonly YouTubeDownloadTool _runner = new();
    public double ReportedDuration { get; set; } = 2.5;
    public bool RefuseAcquisition { get; set; }
    public int DownloadCount { get; private set; }

    public Task<YouTubeDownloadToolResult> RunAsync(YouTubeDownloadToolInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Contains("--skip-download"))
            return Task.FromResult(new YouTubeDownloadToolResult(RefuseAcquisition ? 1 : 0, Samples.Metadata(ReportedDuration).ToJsonString(), ""));
        if (invocation.Arguments.Contains("--extract-audio"))
        {
            Assert.False(RefuseAcquisition, "Existing valid indexed media must not be reacquired.");
            DownloadCount++;
            File.Copy(source, Path.Combine(invocation.WorkingDirectory, "download.m4a"));
            File.Copy(cover, Path.Combine(invocation.WorkingDirectory, "download.jpg"));
            return Task.FromResult(new YouTubeDownloadToolResult(0, "", ""));
        }
        return _runner.RunAsync(invocation, token);
    }
}
