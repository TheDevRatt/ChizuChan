using System.Text.Json.Nodes;
using ChizuChan.Options;
using ChizuChan.Services;

namespace ChizuChan.LongMedia.Regression.Tests;

public sealed class MetadataAcceptanceTests
{
    [Theory]
    [InlineData(900)]
    [InlineData(901)]
    [InlineData(14400)]
    [InlineData(14401)]
    [InlineData(21600)]
    [InlineData(21601)]
    [InlineData(25200)]
    public void Default_policy_accepts_finite_media_without_duration_ceiling(double seconds)
    {
        var accepted = Parse(Samples.Metadata(seconds), new(), out var rejection);
        Assert.True(accepted, $"Finite {seconds}s media rejected by default: {rejection}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_configured_duration_disables_legacy_gate(int disabled)
    {
        Assert.True(Parse(Samples.Metadata(25200), new() { MaxDurationSeconds = disabled }, out var rejection), rejection);
    }

    [Theory]
    [InlineData(21601)]
    [InlineData(25200)]
    public void Explicit_duration_budget_above_six_hours_has_no_hidden_clamp(int budget)
    {
        var options = new YouTubeMusicDownloadOptions { MaxDurationSeconds = budget };
        Assert.True(Parse(Samples.Metadata(budget), options, out var rejection), rejection);
        Assert.False(Parse(Samples.Metadata(budget + 1), options, out _));
    }

    [Theory]
    [InlineData(299, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void Explicit_positive_duration_budget_remains_enforceable(int duration, bool expected)
    {
        Assert.Equal(expected, Parse(Samples.Metadata(duration), new() { MaxDurationSeconds = 300 }, out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("\"25200\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("1e999")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    public void Malformed_or_nonfinite_duration_is_rejected_even_with_policy_disabled(string durationJson)
    {
        var json = Samples.Metadata().ToJsonString().Replace("\"duration\":180", "\"duration\":" + durationJson);
        Assert.False(YouTubeMusicActionHandler.TryParseMetadata(json, Samples.VideoId,
            new() { MaxDurationSeconds = 0 }, out _, out _));
    }

    [Fact]
    public void Missing_duration_is_rejected()
    {
        var metadata = Samples.Metadata();
        metadata.Remove("duration");
        Assert.False(Parse(metadata, new() { MaxDurationSeconds = 0 }, out _));
    }

    [Fact]
    public void Malformed_duration_is_not_reported_as_a_long_track()
    {
        var metadata = Samples.Metadata();
        metadata["duration"] = null;
        Assert.False(Parse(metadata, new(), out var rejection));
        Assert.DoesNotContain("too long", rejection, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("is_live")]
    [InlineData("is_upcoming")]
    [InlineData("post_live")]
    [InlineData("unknown")]
    [InlineData("")]
    public void Active_upcoming_or_not_finalized_stream_is_rejected(string status)
    {
        var metadata = Samples.Metadata(30);
        metadata["live_status"] = status;
        Assert.False(Parse(metadata, new() { MaxDurationSeconds = 0 }, out _));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(25200)]
    public void Finalized_finite_archive_is_accepted(double seconds)
    {
        var metadata = Samples.Metadata(seconds);
        metadata["live_status"] = "was_live";
        metadata["was_live"] = true;
        Assert.True(Parse(metadata, new(), out var rejection), rejection);
    }

    [Theory]
    [InlineData("is_live", "\"true\"")]
    [InlineData("is_live", "1")]
    [InlineData("was_live", "[]")]
    [InlineData("was_live", "{}")]
    public void Malformed_live_flags_are_rejected_instead_of_silently_treated_as_false(string field, string value)
    {
        var metadata = Samples.Metadata(30);
        metadata[field] = JsonNode.Parse(value);
        Assert.False(Parse(metadata, new() { MaxDurationSeconds = 0 }, out _), $"Malformed {field} was accepted: {value}");
    }

    [Fact]
    public void Archive_without_finite_duration_is_rejected()
    {
        var metadata = Samples.Metadata();
        metadata["live_status"] = "was_live";
        metadata["was_live"] = true;
        metadata["duration"] = null;
        Assert.False(Parse(metadata, new() { MaxDurationSeconds = 0 }, out _));
    }

    [Theory]
    [InlineData("is_live", "true")]
    [InlineData("_type", "\"playlist\"")]
    [InlineData("extractor", "\"generic\"")]
    [InlineData("extractor_key", "\"Vimeo\"")]
    [InlineData("id", "\"ABCDEFGHI_0\"")]
    public void Identity_playlist_and_live_guards_survive_relaxed_budgets(string field, string value)
    {
        var metadata = Samples.Metadata(30);
        metadata[field] = JsonNode.Parse(value);
        Assert.False(Parse(metadata, new() { MaxDurationSeconds = 0 }, out _));
    }

    private static bool Parse(JsonObject metadata, YouTubeMusicDownloadOptions options, out string rejection) =>
        YouTubeMusicActionHandler.TryParseMetadata(metadata.ToJsonString(), Samples.VideoId, options, out _, out rejection);
}

public sealed class OptionalBudgetAcceptanceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Disabled_budgets_do_not_emit_legacy_duration_or_filesize_arguments(int disabled)
    {
        var args = YouTubeMusicActionHandler.BuildDownloadArguments(Samples.Url, "/tmp/stage with spaces", "/tools/ffmpeg", disabled, disabled);
        Assert.DoesNotContain(args, argument => argument.Contains("duration", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("--max-filesize", args);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Disabled_size_budget_does_not_emit_max_filesize_argument(int disabled)
    {
        var args = YouTubeMusicActionHandler.BuildDownloadArguments(Samples.Url, "/tmp/stage", "/tools/ffmpeg", 0, disabled);
        Assert.DoesNotContain("--max-filesize", args);
    }

    [Fact]
    public async Task Default_acquisition_arguments_do_not_reintroduce_a_duration_gate()
    {
        using var fixture = new PipelineFixture();
        Assert.True((await fixture.Run()).Success);
        Assert.DoesNotContain(fixture.Tool.Download.Arguments, argument => argument.Contains("duration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Default_acquisition_arguments_do_not_reintroduce_a_size_gate()
    {
        using var fixture = new PipelineFixture();
        Assert.True((await fixture.Run()).Success);
        Assert.DoesNotContain("--max-filesize", fixture.Tool.Download.Arguments);
    }

    [Fact]
    public void Archive_provenance_is_not_an_unconditional_download_filter()
    {
        var args = YouTubeMusicActionHandler.BuildDownloadArguments(Samples.Url, "/tmp/stage", "/tools/ffmpeg", 0, 0);
        Assert.DoesNotContain(args, argument => argument.Contains("!was_live", StringComparison.Ordinal));
    }

    [Fact]
    public void Explicit_large_size_budget_is_not_clamped_to_one_GiB()
    {
        const long budget = 2L * 1024 * 1024 * 1024;
        Assert.Equal(budget, new YouTubeMusicDownloadOptions { MaxFileSizeBytes = budget }.GetMaxFileSizeBytes());
    }

    [Fact]
    public void Explicit_processing_budget_is_not_clamped_to_one_hour()
    {
        Assert.Equal(7200, new YouTubeMusicDownloadOptions { DownloadTimeoutSeconds = 7200 }.GetDownloadTimeoutSeconds());
    }

    [Fact]
    public async Task Default_full_track_stages_have_no_arbitrary_processing_deadline()
    {
        using var fixture = new PipelineFixture();
        var result = await fixture.Run();
        Assert.True(result.Success, result.Message);
        foreach (var stage in new[] { fixture.Tool.Download, fixture.Tool.Tag })
            Assert.True(stage.Timeout == Timeout.InfiniteTimeSpan,
                $"Healthy full-track stage still has an unconditional {stage.Timeout.TotalSeconds}s deadline. Probe/lock budgets may remain finite.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Nonpositive_processing_budget_means_disabled_not_ten_second_clamp(int disabled)
    {
        using var fixture = new PipelineFixture();
        fixture.Options.DownloadTimeoutSeconds = disabled;
        var result = await fixture.Run();
        Assert.True(result.Success, result.Message);
        Assert.Equal(Timeout.InfiniteTimeSpan, fixture.Tool.Download.Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, fixture.Tool.Tag.Timeout);
    }

    [Fact]
    public async Task Explicit_processing_budget_remains_available()
    {
        using var fixture = new PipelineFixture();
        fixture.Options.DownloadTimeoutSeconds = 120;
        var result = await fixture.Run();
        Assert.True(result.Success, result.Message);
        Assert.Equal(TimeSpan.FromSeconds(120), fixture.Tool.Download.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(120), fixture.Tool.Tag.Timeout);
    }
}
