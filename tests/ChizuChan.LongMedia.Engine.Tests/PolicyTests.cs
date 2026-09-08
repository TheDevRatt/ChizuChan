using System.Text.Json;
using ChizuChan.Options;
using ChizuChan.Services;

namespace ChizuChan.LongMedia.Engine.Tests;

public class PolicyTests
{
    public const string Id = "abcdefghijk";
    public static string Metadata(object? duration, string status = "not_live", bool wasLive = false, object? isLive = null) => JsonSerializer.Serialize(new {
        extractor = "youtube", extractor_key = "Youtube", id = Id, live_status = status,
        is_live = isLive ?? false, was_live = wasLive, duration, title = "Test track", artist = "Test artist"
    });

    [Theory]
    [InlineData(14400)] [InlineData(14401)] [InlineData(21601)] [InlineData(25200)]
    public void DefaultPolicyAcceptsFiniteLongMedia(int seconds) =>
        Assert.True(YouTubeMusicActionHandler.TryParseMetadata(Metadata(seconds), Id, new(), out _, out _));

    [Fact]
    public void DefaultPoliciesAndExampleDisableArbitraryCaps()
    {
        var options = new YouTubeMusicDownloadOptions();
        Assert.Equal(0, options.GetMaxDurationSeconds());
        Assert.Equal(0, options.GetMaxFileSizeBytes());
        Assert.Equal(0, options.GetDownloadTimeoutSeconds());
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.example.json")));
        var config = doc.RootElement.GetProperty("YouTubeMusicDownload");
        foreach (var name in new[] { "MaxDurationSeconds", "MaxFileSizeBytes", "DownloadTimeoutSeconds" })
            Assert.Equal(0, config.GetProperty(name).GetInt64());
    }

    [Fact]
    public void ExplicitLimitsAreNotClamped()
    {
        var options = new YouTubeMusicDownloadOptions { MaxDurationSeconds = 36000, MaxFileSizeBytes = 3L * 1024 * 1024 * 1024, DownloadTimeoutSeconds = 7200 };
        Assert.Equal(36000, options.GetMaxDurationSeconds());
        Assert.Equal(3L * 1024 * 1024 * 1024, options.GetMaxFileSizeBytes());
        Assert.Equal(7200, options.GetDownloadTimeoutSeconds());
        Assert.True(YouTubeMusicActionHandler.TryParseMetadata(Metadata(25200), Id, options, out _, out _));
        Assert.False(YouTubeMusicActionHandler.TryParseMetadata(Metadata(36001), Id, options, out _, out var error));
        Assert.Contains("too long", error);
    }

    [Theory]
    [InlineData(null)] [InlineData(0)] [InlineData(-1)] [InlineData("25200")]
    public void InvalidDurationIsNotReportedAsTooLong(object? duration)
    {
        Assert.False(YouTubeMusicActionHandler.TryParseMetadata(Metadata(duration), Id, new(), out _, out var error));
        Assert.Contains("invalid", error);
    }

    [Fact]
    public void CompletedFiniteArchiveIsAllowed() =>
        Assert.True(YouTubeMusicActionHandler.TryParseMetadata(Metadata(25200, "was_live", true), Id, new(), out _, out _));

    [Theory]
    [InlineData("is_live")] [InlineData("is_upcoming")] [InlineData("post_live")] [InlineData("unknown")]
    public void UnboundedOrNotYetCompletedStreamIsRejected(string status) =>
        Assert.False(YouTubeMusicActionHandler.TryParseMetadata(Metadata(60, status), Id, new(), out _, out _));

    [Fact]
    public void MalformedLiveFlagsAreInvalidMetadata()
    {
        Assert.False(YouTubeMusicActionHandler.TryParseMetadata(Metadata(60, isLive: "false"), Id, new(), out _, out var error));
        Assert.Contains("invalid", error);
    }

    [Fact]
    public void DisabledCapsAreAbsentFromDownloaderArguments()
    {
        var args = YouTubeMusicActionHandler.BuildDownloadArguments("https://www.youtube.com/watch?v=" + Id, "/stage", "ffmpeg", 0, 0);
        Assert.DoesNotContain("--max-filesize", args);
        Assert.DoesNotContain(args, arg => arg.Contains("duration <="));
        Assert.DoesNotContain(args, arg => arg.Contains("!was_live"));
        Assert.Contains("--ignore-config", args);
        Assert.Contains("--no-playlist", args);
    }

    [Fact]
    public async Task DownloaderFiltersAreValidForRealYtDlpAndAdmitOnlyFiniteCompletedItems()
    {
        var args = YouTubeMusicActionHandler.BuildDownloadArguments("url", "/stage", "ffmpeg", 36000, 0);
        var filters = args.Select((arg, index) => (arg, index)).Where(p => p.arg == "--match-filter").Select(p => args[p.index + 1]).ToArray();
        var code = "import json; from yt_dlp.utils import match_filter_func; f=match_filter_func(json.loads(" + JsonSerializer.Serialize(JsonSerializer.Serialize(filters)) + ")); " +
            "assert f({'is_live':False,'live_status':'not_live','duration':25200}) is None; " +
            "assert f({'is_live':False,'live_status':'was_live','was_live':True,'duration':25200}) is None; " +
            "assert f({'is_live':True,'live_status':'is_live','duration':25200}) is not None; " +
            "assert f({'is_live':False,'live_status':'is_upcoming','duration':25200}) is not None; " +
            "assert f({'is_live':False,'live_status':'post_live','duration':25200}) is not None; " +
            "assert f({'is_live':False,'live_status':'was_live','duration':36001}) is not None; print('OK')";
        var result = await new YouTubeDownloadTool().RunAsync(ProcessTests.Child(code), default);
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    [Theory]
    [InlineData("_type", "42")]
    [InlineData("live_status", "42")]
    [InlineData("is_live", "42")]
    [InlineData("was_live", "\"false\"")]
    public void MalformedStructuralMetadataIsRejectedAccurately(string field, string value)
    {
        var data = System.Text.Json.Nodes.JsonNode.Parse(Metadata(60))!;
        data[field] = System.Text.Json.Nodes.JsonNode.Parse(value);
        Assert.False(YouTubeMusicActionHandler.TryParseMetadata(data.ToJsonString(), Id, new(), out _, out var error));
        Assert.Contains("invalid", error);
    }

    [Fact]
    public void OptionalCapsArePassedExactly()
    {
        var args = YouTubeMusicActionHandler.BuildDownloadArguments("url", "/stage", "ffmpeg", 36000, 3221225472);
        Assert.Contains("3221225472", args);
        Assert.Contains(args, arg => arg.Contains("duration <= 36000"));
    }
}
