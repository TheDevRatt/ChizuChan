namespace ChizuChan.Services.Interfaces;

public sealed record YouTubeDownloadToolInvocation(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    int MaximumStandardOutputCharacters,
    int MaximumStandardErrorCharacters);

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
