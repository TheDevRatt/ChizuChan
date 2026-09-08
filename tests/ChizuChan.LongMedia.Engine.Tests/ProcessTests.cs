using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using System.Diagnostics;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace ChizuChan.LongMedia.Engine.Tests;

public class ProcessTests
{
    // Real small subprocesses, cross-platform python executable configurable for Windows CI.
    public static string Python => Environment.GetEnvironmentVariable("CHIZU_TEST_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "/usr/bin/python3");
    public static YouTubeDownloadToolInvocation Child(string code, TimeSpan? timeout = null) => new(
        Python, new[] { "-c", code }, Path.GetTempPath(), timeout ?? TimeSpan.FromSeconds(10), 1024, 1024);
    public static YouTubeDownloadToolInvocation Progress(YouTubeDownloadToolInvocation invocation)
    {
        var property = typeof(YouTubeDownloadToolInvocation).GetProperty("OutputMode");
        Assert.NotNull(property);
        property.SetValue(invocation, Enum.Parse(property.PropertyType, "Diagnostics"));
        return invocation;
    }

    [Fact]
    public async Task DiagnosticFloodDrainsBothPipesWithBoundedTail()
    {
        var result = await new YouTubeDownloadTool().RunAsync(Progress(Child("import sys; sys.stdout.write('x'*2000000+'OUT-END'); sys.stderr.write('y'*2000000+'ERR-END')")), default);
        Assert.Equal(0, result.ExitCode);
        Assert.InRange(result.StandardOutput.Length, 7, 1024);
        Assert.InRange(result.StandardError.Length, 7, 1024);
        Assert.EndsWith("OUT-END", result.StandardOutput);
        Assert.EndsWith("ERR-END", result.StandardError);
    }

    [Fact]
    public async Task MetadataStderrFloodIsNonFatal()
    {
        var result = await new YouTubeDownloadTool().RunAsync(Child("import sys; sys.stderr.write('x'*2000000); print('{}')"), default);
        Assert.Equal("{}", result.StandardOutput.Trim());
        Assert.Equal(1024, result.StandardError.Length);
    }

    [Fact]
    public async Task MetadataStdoutOverflowRemainsFatal()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new YouTubeDownloadTool().RunAsync(Child("print('x'*2000000)"), default));
    }

    [Fact]
    public async Task ZeroTimeoutMeansNoElapsedCutoff()
    {
        var result = await new YouTubeDownloadTool().RunAsync(Child("import time; time.sleep(.2); print('done')", TimeSpan.Zero), default);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task CancellationKillsChildTreeAndReleasesSlot()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".pid");
        try
        {
            var escaped = System.Text.Json.JsonSerializer.Serialize(pidFile);
            using var cancel = new CancellationTokenSource();
            var task = new YouTubeDownloadTool().RunAsync(Child($"import subprocess,sys,time; p=subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)']); open({escaped},'w').write(str(p.pid)); time.sleep(60)"), cancel.Token);
            await WaitForFile(pidFile);
            var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            await Task.Delay(100);
            Assert.False(IsRunning(pid));
            Assert.Equal(0, (await new YouTubeDownloadTool().RunAsync(Child("print('slot released')"), default)).ExitCode);
        }
        finally { File.Delete(pidFile); }
    }

    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!OperatingSystem.IsWindows() && File.ReadAllText($"/proc/{pid}/stat").Split(' ')[2] == "Z") return false;
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (FileNotFoundException) { return false; }
    }
    internal static async Task WaitForFile(string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path) || new FileInfo(path).Length == 0) await Task.Delay(20, deadline.Token);
    }

    [Fact]
    public void WindowsInvocationUsesArgumentListNoShellOrWindow()
    {
        var start = YouTubeDownloadTool.CreateStartInfo(new("C:\\Program Files\\ffmpeg.exe", ["-i", "C:\\media\\some & file.m4a"], "C:\\media", TimeSpan.Zero, 32, 32));
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal("C:\\media\\some & file.m4a", start.ArgumentList[1]);
    }
}
