using ChizuChan.Services;

namespace ChizuChan.LongMedia.Regression.Tests;

// Large fixture bytes are sparse container headers, NOT audio and NOT proof of decoding.
public sealed class SparsePipelineAcceptanceTests
{
    [Theory]
    [InlineData(14400)]
    [InlineData(14401)]
    [InlineData(21601)]
    [InlineData(25200)]
    public async Task Finite_long_metadata_reaches_atomic_promotion_at_default_budgets(int duration)
    {
        using var fixture = new PipelineFixture();
        fixture.Tool.Metadata = Samples.Metadata(duration);
        var result = await fixture.Run();
        Assert.True(result.Success, $"{duration}s import failed: {result.Message}");
        Assert.Single(fixture.Media);
        fixture.AssertCleanStaging();
    }

    [Theory]
    [InlineData(460800000L)]
    [InlineData(1073741825L)]
    public async Task Default_size_policy_accepts_sparse_size_fixture_without_hidden_ceiling(long length)
    {
        using var fixture = new PipelineFixture();
        fixture.Tool.DownloadBytes = length;
        fixture.Tool.FinalBytes = length;
        var result = await fixture.Run();
        Assert.True(result.Success, $"Sparse size-policy fixture of {length} bytes was rejected: {result.Message}");
        Assert.Equal(length, new FileInfo(Assert.Single(fixture.Media)).Length);
        fixture.AssertCleanStaging();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Disabled_size_policy_does_not_reject_sparse_size_fixture(int disabled)
    {
        using var fixture = new PipelineFixture();
        fixture.Options.MaxFileSizeBytes = disabled;
        fixture.Tool.DownloadBytes = 2 * 1024 * 1024;
        fixture.Tool.FinalBytes = 2 * 1024 * 1024;
        var result = await fixture.Run();
        Assert.True(result.Success, result.Message);
        fixture.AssertCleanStaging();
    }

    [Theory]
    [InlineData(2097151L, 4096L, true)]
    [InlineData(2097152L, 4096L, true)]
    [InlineData(2097153L, 4096L, false)]
    [InlineData(4096L, 2097151L, true)]
    [InlineData(4096L, 2097152L, true)]
    [InlineData(4096L, 2097153L, false)]
    public async Task Explicit_size_budget_bounds_acquired_and_final_files(long downloaded, long tagged, bool accepted)
    {
        using var fixture = new PipelineFixture();
        fixture.Options.MaxFileSizeBytes = 2097152;
        fixture.Tool.DownloadBytes = downloaded;
        fixture.Tool.FinalBytes = tagged;
        var result = await fixture.Run();
        Assert.Equal(accepted, result.Success);
        if (accepted) Assert.Single(fixture.Media); else Assert.Empty(fixture.Media);
        fixture.AssertCleanStaging();
    }

    [Fact]
    public async Task Canonical_single_video_arguments_ignore_external_configuration()
    {
        using var fixture = new PipelineFixture();
        var result = await fixture.Run();
        Assert.True(result.Success, result.Message);
        foreach (var invocation in fixture.Tool.Calls.Where(c => c.Arguments.Contains("--ignore-config")))
        {
            Assert.Contains("--no-playlist", invocation.Arguments);
            Assert.Equal(Samples.Url, invocation.Arguments[^1]);
            var start = YouTubeDownloadTool.CreateStartInfo(invocation);
            Assert.False(start.UseShellExecute);
            Assert.Equal(invocation.Arguments, start.ArgumentList.ToArray());
        }
        Assert.True(YouTubeMusicPathPolicy.IsWithinRoot(fixture.Root, Assert.Single(fixture.Media)));
    }

    [Fact]
    public async Task Failed_tagging_does_not_publish_partial_media_or_index()
    {
        using var fixture = new PipelineFixture();
        fixture.Tool.FailTag = true;
        Assert.False((await fixture.Run()).Success);
        Assert.Empty(fixture.Media);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.json", SearchOption.AllDirectories));
        fixture.AssertCleanStaging();
    }

    [Fact]
    public async Task Repeated_import_reuses_index_without_reacquiring_or_overwriting()
    {
        using var fixture = new PipelineFixture();
        Assert.True((await fixture.Run()).Success);
        var path = Assert.Single(fixture.Media);
        var before = File.ReadAllBytes(path);
        fixture.Tool.Calls.Clear();
        Assert.True((await fixture.Run()).Success);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.DoesNotContain(fixture.Tool.Calls, c => c.Arguments.Contains("--extract-audio"));
    }

    [Fact]
    public async Task Nonexclusive_root_is_denied_before_tool_execution()
    {
        using var fixture = new PipelineFixture();
        fixture.Options.RequireExclusiveLibraryRoot = false;
        Assert.False((await fixture.Run()).Success);
        Assert.Empty(fixture.Tool.Calls);
    }

    [Fact]
    public async Task Traversal_video_id_is_denied_before_tool_execution()
    {
        using var fixture = new PipelineFixture();
        Assert.False((await fixture.Handler.HandleAsync(42, "../escape")).Success);
        Assert.Empty(fixture.Tool.Calls);
    }

    [Fact]
    public async Task Symlinked_library_is_denied_without_writing_outside_root()
    {
        using var fixture = new PipelineFixture();
        using var outside = new ScratchDirectory();
        Directory.CreateSymbolicLink(fixture.Root, outside.Path);
        Assert.False((await fixture.Run()).Success);
        Assert.Empty(fixture.Tool.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }
}
