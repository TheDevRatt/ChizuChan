using System.Diagnostics;
using System.Text;
using ChizuChan.Services.Interfaces;

namespace ChizuChan.Services;

public sealed class YouTubeDownloadTool : IYouTubeDownloadTool
{
    private static readonly SemaphoreSlim ProcessSlots = new(2, 2);
    private readonly Func<string, long> _freeSpace;

    public YouTubeDownloadTool() : this(YouTubeLongMediaResourceMonitor.AvailableFreeSpace) { }
    public YouTubeDownloadTool(Func<string, long> freeSpace) => _freeSpace = freeSpace;

    public async Task<YouTubeDownloadToolResult> RunAsync(
        YouTubeDownloadToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        // Queue waiting is cancelable, but does not spend a process's optional elapsed budget.
        await ProcessSlots.WaitAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var monitor = new YouTubeLongMediaResourceMonitor(invocation, _freeSpace);
            monitor.CheckSpace();
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

            var stdout = ReadOutputAsync(
                process.StandardOutput,
                invocation.MaximumStandardOutputCharacters,
                invocation.OutputMode == YouTubeDownloadOutputMode.Metadata,
                monitor.Activity, deadline.Token);
            var stderr = ReadOutputAsync(
                process.StandardError,
                invocation.MaximumStandardErrorCharacters,
                strict: false,
                monitor.Activity, deadline.Token);
            var monitoring = monitor.WatchAsync(process, deadline.Token);

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
                    var completed = await Task.WhenAny(pending.Append(monitoring));
                    await completed;
                    pending.Remove(completed);
                }
                monitor.CheckSpace();
                return new YouTubeDownloadToolResult(process.ExitCode, await stdout, await stderr);
            }
            catch
            {
                deadline.Cancel();
                await StopAndDrainAsync(process, stdout, stderr);
                throw;
            }
            finally
            {
                deadline.Cancel();
                try { await monitoring; } catch { } // Observe cancellation/fault, preserving the original cause.
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

    private static async Task<string> ReadOutputAsync(
        TextReader reader, int maximumCharacters, bool strict, Action activity, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        var retained = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return retained.ToString();
            activity();
            if (strict && read > maximumCharacters - retained.Length)
                throw new InvalidDataException("Media metadata output exceeded the allowed size.");
            var keep = Math.Min(read, maximumCharacters);
            var remove = Math.Max(0, retained.Length + keep - maximumCharacters);
            if (remove > 0) retained.Remove(0, remove);
            retained.Append(buffer, read - keep, keep);
        }
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
