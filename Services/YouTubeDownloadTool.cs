using System.Diagnostics;
using ChizuChan.Services.Interfaces;

namespace ChizuChan.Services;

public sealed class YouTubeDownloadTool : IYouTubeDownloadTool
{
    private static readonly SemaphoreSlim ProcessSlots = new(2, 2);

    public async Task<YouTubeDownloadToolResult> RunAsync(
        YouTubeDownloadToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(invocation.Timeout);
        await ProcessSlots.WaitAsync(deadline.Token);
        try
        {
            using var process = new Process
            {
                StartInfo = CreateStartInfo(invocation),
                EnableRaisingEvents = true,
            };

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Media tool could not start.");
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new FileNotFoundException("A required media tool is unavailable.", exception);
            }

            var stdout = YtDlpSearchRunner.ReadBoundedAsync(
                process.StandardOutput,
                invocation.MaximumStandardOutputCharacters,
                deadline.Token);
            var stderr = YtDlpSearchRunner.ReadBoundedAsync(
                process.StandardError,
                invocation.MaximumStandardErrorCharacters,
                deadline.Token);

            try
            {
                var pending = new List<Task>
                {
                    process.WaitForExitAsync(deadline.Token),
                    stdout,
                    stderr,
                };
                while (pending.Count > 0)
                {
                    var completed = await Task.WhenAny(pending);
                    await completed;
                    pending.Remove(completed);
                }
                return new YouTubeDownloadToolResult(process.ExitCode, await stdout, await stderr);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await StopAndDrainAsync(process, stdout, stderr);
                throw new TimeoutException("Media tool timed out.");
            }
            catch
            {
                await StopAndDrainAsync(process, stdout, stderr);
                throw;
            }
        }
        finally
        {
            ProcessSlots.Release();
        }
    }

    public static ProcessStartInfo CreateStartInfo(YouTubeDownloadToolInvocation invocation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.ExecutablePath,
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in invocation.Arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static async Task StopAndDrainAsync(
        Process process,
        Task<string> stdout,
        Task<string> stderr)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
        try { process.StandardOutput.Close(); } catch { }
        try { process.StandardError.Close(); } catch { }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await process.WaitForExitAsync(cleanup.Token); } catch { }
        try { await Task.WhenAll(stdout, stderr).WaitAsync(cleanup.Token); } catch { }
    }
}
