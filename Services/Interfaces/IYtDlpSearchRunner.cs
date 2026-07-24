namespace ChizuChan.Services.Interfaces;

public sealed record YtDlpSearchProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IYtDlpSearchRunner
{
    Task<YtDlpSearchProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
