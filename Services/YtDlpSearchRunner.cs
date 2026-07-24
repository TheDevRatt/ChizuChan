using System.Diagnostics;
using System.Text;
using ChizuChan.Services.Interfaces;

namespace ChizuChan.Services;

public sealed class YtDlpSearchRunner : IYtDlpSearchRunner
{
    private const int MaximumStandardOutputCharacters = 512 * 1024;
    private const int MaximumStandardErrorCharacters = 32 * 1024;
    private static readonly SemaphoreSlim ProcessSlots = new(2, 2);

    public async Task<YtDlpSearchProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        if (!await ProcessSlots.WaitAsync(TimeSpan.Zero, deadline.Token))
            throw new InvalidOperationException("YouTube search is busy.");

        try
        {
            var executable = ResolveExecutable();
            using var process = new Process
            {
                StartInfo = CreateStartInfo(executable, arguments),
                EnableRaisingEvents = true,
            };

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Failed to start yt-dlp.");
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new FileNotFoundException("yt-dlp is not installed or available beside ChizuChan.", exception);
            }

            var stdoutTask = ReadBoundedAsync(
                process.StandardOutput,
                MaximumStandardOutputCharacters,
                deadline.Token);
            var stderrTask = ReadBoundedAsync(
                process.StandardError,
                MaximumStandardErrorCharacters,
                deadline.Token);
            var exitTask = process.WaitForExitAsync(deadline.Token);

            try
            {
                var pending = new List<Task> { exitTask, stdoutTask, stderrTask };
                while (pending.Count > 0)
                {
                    var completed = await Task.WhenAny(pending);
                    await completed;
                    pending.Remove(completed);
                }

                return new YtDlpSearchProcessResult(
                    process.ExitCode,
                    await stdoutTask,
                    await stderrTask);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await StopAndDrainAsync(process, stdoutTask, stderrTask);
                throw new TimeoutException("yt-dlp search timed out.");
            }
            catch
            {
                await StopAndDrainAsync(process, stdoutTask, stderrTask);
                throw;
            }
        }
        finally
        {
            ProcessSlots.Release();
        }
    }

    public static ProcessStartInfo CreateStartInfo(
        string executable,
        IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    public static async Task<string> ReadBoundedAsync(
        TextReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);

        var output = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                return output.ToString();

            if (output.Length + read > maximumCharacters)
                throw new InvalidDataException("yt-dlp output exceeded the allowed size.");

            output.Append(buffer, 0, read);
        }
    }

    private static string ResolveExecutable()
    {
        var localExecutable = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        return File.Exists(localExecutable) ? localExecutable : "yt-dlp";
    }

    private static async Task StopAndDrainAsync(
        Process process,
        Task<string> stdout,
        Task<string> stderr)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The parent may already have exited. Closing the process streams below
            // still releases readers inherited by an anomalous descendant.
        }

        try { process.StandardOutput.Close(); } catch { }
        try { process.StandardError.Close(); } catch { }

        using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await process.WaitForExitAsync(cleanupDeadline.Token); } catch { }
        try { await Task.WhenAll(stdout, stderr).WaitAsync(cleanupDeadline.Token); } catch { }
    }
}
