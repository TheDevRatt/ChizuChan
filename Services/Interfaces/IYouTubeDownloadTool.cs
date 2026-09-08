namespace ChizuChan.Services.Interfaces;

public sealed record YouTubeDownloadToolInvocation(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    int MaximumStandardOutputCharacters,
    int MaximumStandardErrorCharacters)
{
    // Existing six-argument invocations remain strict metadata producers.
    public YouTubeDownloadOutputMode OutputMode { get; init; } = YouTubeDownloadOutputMode.Metadata;
    public long MinimumFreeSpaceBytes { get; init; }
    public TimeSpan StalledWorkTimeout { get; init; }
    public TimeSpan MonitoringInterval { get; init; } = TimeSpan.FromSeconds(1);
}

public enum YouTubeDownloadOutputMode
{
    Metadata,
    Diagnostics,
}

public sealed record YouTubeDownloadToolResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IYouTubeDownloadTool
{
    Task<YouTubeDownloadToolResult> RunAsync(
        YouTubeDownloadToolInvocation invocation,
        CancellationToken cancellationToken);
}
