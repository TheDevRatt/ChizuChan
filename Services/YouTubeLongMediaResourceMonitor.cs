using System.Diagnostics;
using ChizuChan.Services.Interfaces;

namespace ChizuChan.Services;

public sealed class YouTubeLongMediaLowSpaceException : IOException
{
    public YouTubeLongMediaLowSpaceException() : base("Insufficient free space in the YouTube library reserve.") { }
}

public sealed class YouTubeLongMediaStalledException : TimeoutException
{
    public YouTubeLongMediaStalledException() : base("Media work stalled without output, file changes or CPU progress.") { }
}

internal sealed class YouTubeLongMediaResourceMonitor
{
    private readonly YouTubeDownloadToolInvocation _invocation;
    private readonly Func<string, long> _freeSpace;
    private long _lastActivity = Stopwatch.GetTimestamp();

    internal YouTubeLongMediaResourceMonitor(YouTubeDownloadToolInvocation invocation, Func<string, long> freeSpace)
    {
        _invocation = invocation;
        _freeSpace = freeSpace;
    }

    internal void Activity() => Interlocked.Exchange(ref _lastActivity, Stopwatch.GetTimestamp());

    internal void CheckSpace()
    {
        if (_invocation.MinimumFreeSpaceBytes > 0 &&
            _freeSpace(_invocation.WorkingDirectory) < _invocation.MinimumFreeSpaceBytes)
            throw new YouTubeLongMediaLowSpaceException();
    }

    internal async Task WatchAsync(Process process, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var files = FileActivity();
        var cpu = TimeSpan.Zero;
        var interval = _invocation.MonitoringInterval > TimeSpan.Zero
            ? _invocation.MonitoringInterval : TimeSpan.FromSeconds(1);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckSpace();
            var currentFiles = FileActivity();
            process.Refresh();
            var currentCpu = process.TotalProcessorTime;
            if (currentFiles != files || currentCpu > cpu) Activity();
            files = currentFiles;
            cpu = currentCpu;
            if (_invocation.Timeout > TimeSpan.Zero && Stopwatch.GetElapsedTime(started) >= _invocation.Timeout)
                throw new TimeoutException("Media tool exceeded the configured elapsed time limit.");
            if (_invocation.StalledWorkTimeout > TimeSpan.Zero &&
                Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivity)) >= _invocation.StalledWorkTimeout)
                throw new YouTubeLongMediaStalledException();
            await Task.Delay(interval, cancellationToken);
        }
    }

    private (long Bytes, long Changes) FileActivity()
    {
        long bytes = 0, changes = 0;
        // Only top-level staging files. Do not follow links or recursively scan a large library.
        foreach (var path in Directory.EnumerateFiles(_invocation.WorkingDirectory))
        {
            try
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                unchecked { bytes += info.Length; changes += info.LastWriteTimeUtc.Ticks; }
            }
            catch (FileNotFoundException) { } // A tool may atomically rename an intermediate file.
        }
        return (bytes, changes);
    }

    internal static long AvailableFreeSpace(string path)
    {
        // DriveInfo accepts directory paths on Unix (statvfs), and drive/UNC roots on Windows.
        return new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(path))! : path)
            .AvailableFreeSpace;
    }
}
