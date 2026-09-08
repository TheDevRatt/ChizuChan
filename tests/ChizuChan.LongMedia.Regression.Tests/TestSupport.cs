using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ChizuChan.Options;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ChizuChan.LongMedia.Regression.Tests;

internal static class Samples
{
    public const string VideoId = "abcdefghijk";
    public const string Url = "https://www.youtube.com/watch?v=" + VideoId;

    public static JsonObject Metadata(double duration = 180) => new()
    {
        ["_type"] = "video", ["extractor"] = "youtube", ["extractor_key"] = "Youtube",
        ["id"] = VideoId, ["duration"] = duration, ["live_status"] = "not_live",
        ["is_live"] = false, ["was_live"] = false,
        ["title"] = "Acceptance tone", ["artist"] = "Local generator",
        ["album"] = "Offline fixtures", ["genre"] = "Test", ["release_year"] = 2024,
    };

    public static string ToolPath(string name)
    {
        var configured = Environment.GetEnvironmentVariable("CHIZU_TEST_" + name.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException($"Test prerequisite missing: {name}. See README.md; do not skip real-process tests.");
    }

    // Deliberately not audio. Only exercises path/size/promotion orchestration with a fake verifier.
    public static void WriteSparseContainerHeader(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        Span<byte> header = stackalloc byte[16];
        header.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(header, 16);
        "ftypM4A "u8.CopyTo(header[4..]);
        stream.Write(header);
        stream.SetLength(length);
    }
}

internal sealed class ScratchDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chizu-acceptance-" + Guid.NewGuid().ToString("N"));
    public ScratchDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal sealed class PipelineFixture : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    public string Root { get; }
    public YouTubeMusicDownloadOptions Options { get; }
    public SyntheticAcquisitionTool Tool { get; }
    public YouTubeMusicActionHandler Handler { get; }

    public PipelineFixture()
    {
        Root = Path.Combine(_scratch.Path, "library");
        var executable = Path.Combine(_scratch.Path, "never-executed-tool");
        File.WriteAllText(executable, "test placeholder, never executed");
        Options = new YouTubeMusicDownloadOptions
        {
            Enabled = true, LibraryRootPath = Root, YtDlpPath = executable, FfmpegPath = executable,
        };
        Tool = new SyntheticAcquisitionTool(Root);
        Handler = new YouTubeMusicActionHandler(Tool, Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<YouTubeMusicActionHandler>.Instance);
    }

    public Task<YouTubeMusicActionResult> Run(CancellationToken token = default) => Handler.HandleAsync(42, Samples.VideoId, token);
    public string[] Media => Directory.Exists(Root) ? Directory.GetFiles(Root, "*.m4a", SearchOption.AllDirectories) : [];
    public void AssertCleanStaging()
    {
        var staging = Path.Combine(Root, ".chizu-staging");
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any(), "Staging leaked after completion/failure.");
    }
    public void Dispose() => _scratch.Dispose();
}

// This fake exists only at the external-tool seam. Real production handler, options, parser,
// locking, file checks, argument construction, index writes and moves are exercised.
internal sealed class SyntheticAcquisitionTool(string root) : IYouTubeDownloadTool
{
    public JsonObject Metadata { get; set; } = Samples.Metadata();
    public long DownloadBytes { get; set; } = 4096;
    public long FinalBytes { get; set; } = 4096;
    public bool FailTag { get; set; }
    public List<YouTubeDownloadToolInvocation> Calls { get; } = [];
    public YouTubeDownloadToolInvocation Download => Calls.First(c => c.Arguments.Contains("--extract-audio"));
    public YouTubeDownloadToolInvocation Tag => Calls.First(c => c.Arguments.Contains("-metadata"));

    public Task<YouTubeDownloadToolResult> RunAsync(YouTubeDownloadToolInvocation invocation, CancellationToken token)
    {
        Calls.Add(invocation);
        token.ThrowIfCancellationRequested();
        var args = invocation.Arguments;
        if (args.Contains("--skip-download"))
            return Task.FromResult(new YouTubeDownloadToolResult(0, Metadata.ToJsonString(), ""));
        if (args.Contains("--extract-audio"))
        {
            Samples.WriteSparseContainerHeader(Path.Combine(invocation.WorkingDirectory, "download.m4a"), DownloadBytes);
        }
        else if (args.Contains("-metadata"))
        {
            Assert.DoesNotContain(Directory.GetFiles(root, "*.m4a", SearchOption.AllDirectories),
                p => !p.Contains(Path.DirectorySeparatorChar + ".chizu-staging" + Path.DirectorySeparatorChar));
            if (FailTag) return Task.FromResult(new YouTubeDownloadToolResult(1, "", "synthetic tag failure"));
            Samples.WriteSparseContainerHeader(args[^1], FinalBytes);
        }
        else if (!args.Contains("null"))
            throw new InvalidOperationException("Unknown tool stage. Update only the test adapter for a candidate's real verification API: " + string.Join(' ', args));
        return Task.FromResult(new YouTubeDownloadToolResult(0, "", ""));
    }
}

internal static class LocalProcess
{
    // Independent fixture generation/measurement, not the runner under test. Hard safety deadline.
    public static async Task<(string Output, string Error)> Run(string executable, IEnumerable<string> arguments, string directory)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        }};
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        var result = (await output, await error);
        Assert.True(process.ExitCode == 0, $"Fixture process {executable} exited {process.ExitCode}: {result.Item2}");
        return result;
    }
}
