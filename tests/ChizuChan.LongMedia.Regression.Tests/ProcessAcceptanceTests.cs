using System.Diagnostics;
using System.Text.Json;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;

namespace ChizuChan.LongMedia.Regression.Tests;

public sealed class ProcessAcceptanceTests
{
    private static string Script => Path.Combine(AppContext.BaseDirectory, "Fixtures", "process_child.py");

    [Theory]
    [InlineData("download", "stdout", 1024)]
    [InlineData("download", "stdout", 1025)]
    [InlineData("download", "stderr", 1025)]
    [InlineData("download", "both", 131072)]
    [InlineData("tag", "stdout", 1025)]
    [InlineData("tag", "stderr", 1024)]
    [InlineData("tag", "stderr", 1025)]
    [InlineData("tag", "both", 131072)]
    public async Task Full_track_progress_finishes_while_retained_diagnostics_stay_bounded(string stage, string stream, int count)
    {
        using var scratch = new ScratchDirectory();
        using var fixture = new PipelineFixture();
        Assert.True((await fixture.Run()).Success);
        // Preserve the handler's real stage policy. Only substitute a local child and small bounds.
        var template = stage == "download" ? fixture.Tool.Download : fixture.Tool.Tag;
        var marker = Path.Combine(scratch.Path, "finished");
        var invocation = Child(template, scratch.Path, ["output", count.ToString(), stream, marker]) with
        {
            MaximumStandardOutputCharacters = 1024, MaximumStandardErrorCharacters = 1024,
        };
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await new YouTubeDownloadTool().RunAsync(invocation, safety.Token);
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(marker), "Capture overflow killed the producer before completion.");
        Assert.InRange(result.StandardOutput.Length, 0, 1024);
        Assert.InRange(result.StandardError.Length, 0, 1024);
        if (stream is "stdout" or "both") Assert.NotEmpty(result.StandardOutput);
        if (stream is "stderr" or "both") Assert.NotEmpty(result.StandardError);
    }

    [Fact]
    public async Task Metadata_stdout_overflow_remains_fatal_not_truncated_into_parseable_data()
    {
        using var scratch = new ScratchDirectory();
        using var fixture = new PipelineFixture();
        Assert.True((await fixture.Run()).Success);
        var probe = fixture.Tool.Calls.First(c => c.Arguments.Contains("--skip-download"));
        var invocation = Child(probe, scratch.Path, ["metadata"]) with
        { MaximumStandardOutputCharacters = 1024 };
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<InvalidDataException>(() => new YouTubeDownloadTool().RunAsync(invocation, safety.Token));
    }

    [Fact]
    public async Task Strict_metadata_reader_accepts_exact_bound_and_rejects_one_more_character()
    {
        const string json = "{\"duration\":25200}";
        Assert.Equal(json, await YtDlpSearchRunner.ReadBoundedAsync(new StringReader(json), json.Length, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => YtDlpSearchRunner.ReadBoundedAsync(new StringReader(json + " "), json.Length, CancellationToken.None));
    }

    [Fact]
    public async Task Disabled_runner_deadline_allows_a_healthy_short_job_to_finish()
    {
        using var scratch = new ScratchDirectory();
        var marker = Path.Combine(scratch.Path, "finished");
        var invocation = Child(Basic(scratch.Path), scratch.Path, ["healthy", marker]) with { Timeout = Timeout.InfiniteTimeSpan };
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await new YouTubeDownloadTool().RunAsync(invocation, safety.Token);
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(marker));
        Assert.Contains("progress", result.StandardOutput);
    }

    [Fact]
    public async Task Explicit_deadline_times_out_and_releases_process_slot()
    {
        using var scratch = new ScratchDirectory();
        var tool = new YouTubeDownloadTool();
        var invocation = Child(Basic(scratch.Path), scratch.Path, ["sleep"]) with { Timeout = TimeSpan.FromMilliseconds(250) };
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<TimeoutException>(() => tool.RunAsync(invocation, safety.Token));
        var marker = Path.Combine(scratch.Path, "next");
        var next = await tool.RunAsync(Child(Basic(scratch.Path), scratch.Path, ["output", "8", "both", marker]), safety.Token);
        Assert.Equal(0, next.ExitCode);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task Caller_cancellation_terminates_parent_and_descendant_and_releases_slot()
    {
        using var scratch = new ScratchDirectory();
        var marker = Path.Combine(scratch.Path, "pids.json");
        var tool = new YouTubeDownloadTool();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = tool.RunAsync(Child(Basic(scratch.Path), scratch.Path, ["tree", marker]), cancellation.Token);
        int[] pids = [];
        try
        {
            await WaitUntil(() => File.Exists(marker) && new FileInfo(marker).Length > 0, TimeSpan.FromSeconds(5));
            pids = JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(marker))!;
            Assert.Equal(2, pids.Length);
            Assert.All(pids, pid => Assert.True(IsRunning(pid), $"Fixture PID {pid} never became live."));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitUntil(() => pids.All(pid => !IsRunning(pid)), TimeSpan.FromSeconds(5));
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var next = await tool.RunAsync(Child(Basic(scratch.Path), scratch.Path,
                ["output", "8", "both", Path.Combine(scratch.Path, "next")]), safety.Token);
            Assert.Equal(0, next.ExitCode);
        }
        finally
        {
            cancellation.Cancel();
            foreach (var pid in pids)
                try { using var process = Process.GetProcessById(pid); if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (ArgumentException) { }
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    [Fact]
    public async Task Already_cancelled_caller_does_not_start_a_child()
    {
        using var scratch = new ScratchDirectory();
        var marker = Path.Combine(scratch.Path, "forbidden");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new YouTubeDownloadTool().RunAsync(
            Child(Basic(scratch.Path), scratch.Path, ["output", "8", "both", marker]), new CancellationToken(true)));
        Assert.False(File.Exists(marker));
    }

    private static YouTubeDownloadToolInvocation Basic(string directory) => new(
        Samples.ToolPath("python3"), [], directory, TimeSpan.FromSeconds(10), 4096, 4096);

    private static YouTubeDownloadToolInvocation Child(YouTubeDownloadToolInvocation template, string directory, string[] args) => template with
    {
        ExecutablePath = Samples.ToolPath("python3"), Arguments = new[] { Script }.Concat(args).ToArray(), WorkingDirectory = directory,
    };

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate() && elapsed.Elapsed < timeout) await Task.Delay(20);
        Assert.True(predicate(), "Timed out waiting for local child readiness/termination.");
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            // A killed orphan may await init reaping on WSL. A zombie cannot execute or hold pipes.
            if (OperatingSystem.IsLinux())
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                if (stat[(stat.LastIndexOf(')') + 2)..].StartsWith("Z ", StringComparison.Ordinal)) return false;
            }
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
