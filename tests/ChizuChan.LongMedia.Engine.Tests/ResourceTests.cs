using ChizuChan.Services;
using ChizuChan.Services.Interfaces;

namespace ChizuChan.LongMedia.Engine.Tests;

public class ResourceTests
{
    private static YouTubeDownloadToolInvocation Configure(YouTubeDownloadToolInvocation invocation, string name, object value)
    {
        var property = invocation.GetType().GetProperty(name);
        Assert.NotNull(property);
        property.SetValue(invocation, value);
        return invocation;
    }
    private static YouTubeDownloadTool Tool(Func<string, long> freeSpace)
    {
        var constructor = typeof(YouTubeDownloadTool).GetConstructor([typeof(Func<string, long>)]);
        Assert.NotNull(constructor);
        return (YouTubeDownloadTool)constructor.Invoke([freeSpace]);
    }
    private static YouTubeDownloadToolInvocation Monitored(string code, long reserve = 100, double stallSeconds = 0) =>
        Configure(Configure(Configure(ProcessTests.Child(code, TimeSpan.Zero), "MinimumFreeSpaceBytes", reserve),
            "StalledWorkTimeout", TimeSpan.FromSeconds(stallSeconds)), "MonitoringInterval", TimeSpan.FromMilliseconds(30));

    [Fact]
    public async Task ExplicitElapsedLimitStopsAndClassifiesARealChild()
    {
        var invocation = ProcessTests.Child("import time; time.sleep(60)", TimeSpan.FromMilliseconds(200)) with
        {
            MonitoringInterval = TimeSpan.FromMilliseconds(30),
        };
        var error = await Assert.ThrowsAsync<TimeoutException>(() => new YouTubeDownloadTool().RunAsync(invocation, default));
        Assert.Contains("elapsed time limit", error.Message);
        Assert.Equal(0, (await new YouTubeDownloadTool().RunAsync(ProcessTests.Child("print('OK')"), default)).ExitCode);
    }

    [Fact]
    public async Task FinalDiskCheckClassifiesSpaceExhaustionBetweenSamples()
    {
        var reads = 0;
        var invocation = Monitored("print('OK')") with { MonitoringInterval = TimeSpan.FromSeconds(10) };
        var error = await Assert.ThrowsAnyAsync<IOException>(() => Tool(_ => Interlocked.Increment(ref reads) < 3 ? 200 : 99).RunAsync(invocation, default));
        Assert.Contains("free space", error.Message);
    }

    [Fact]
    public async Task LowSpaceDenialHappensBeforeProcessStart()
    {
        var marker = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".marker");
        try
        {
            var error = await Assert.ThrowsAnyAsync<IOException>(() => Tool(_ => 99).RunAsync(Monitored($"open({System.Text.Json.JsonSerializer.Serialize(marker)},'w').write('started')"), default));
            Assert.Contains("free space", error.Message);
            Assert.False(File.Exists(marker));
        }
        finally { File.Delete(marker); }
    }

    [Fact]
    public async Task LiveSpaceDropStopsChildAndReleasesSlot()
    {
        var reads = 0;
        var tool = Tool(_ => Interlocked.Increment(ref reads) < 4 ? 200 : 99);
        var error = await Assert.ThrowsAnyAsync<IOException>(() => tool.RunAsync(Monitored("import time; time.sleep(60)"), default));
        Assert.Contains("free space", error.Message);
        Assert.True(reads >= 4);
        Assert.Equal(0, (await Tool(_ => 200).RunAsync(Monitored("print('OK')"), default)).ExitCode);
    }

    [Fact]
    public async Task SilentIdleWorkIsClassifiedAsStalledNotElapsedTimeout()
    {
        var error = await Assert.ThrowsAnyAsync<TimeoutException>(() => Tool(_ => 200).RunAsync(Monitored("import time; time.sleep(60)", stallSeconds: .25), default));
        Assert.Contains("stalled", error.Message);
    }

    [Fact]
    public async Task HealthyProgressOutlivesStallWindow()
    {
        var result = await Tool(_ => 200).RunAsync(Monitored("import time; [(print(i,flush=True), time.sleep(.08)) for i in range(12)]", stallSeconds: .4), default);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task SilentFileGrowthOutlivesStallWindow()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var invocation = Monitored("import time; f=open('growing.part','wb',buffering=0); [(f.write(b'x'),time.sleep(.08)) for i in range(12)]", stallSeconds: .4) with { WorkingDirectory = dir };
            Assert.Equal(0, (await Tool(_ => 200).RunAsync(invocation, default)).ExitCode);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task QueuedProcessDoesNotConsumeElapsedStageBudget()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        using var cancel = new CancellationTokenSource();
        var tasks = new List<Task<YouTubeDownloadToolResult>>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var marker = Path.Combine(dir, i.ToString());
                tasks.Add(new YouTubeDownloadTool().RunAsync(ProcessTests.Child($"import time; open({System.Text.Json.JsonSerializer.Serialize(marker)},'w').write('ready'); time.sleep(60)"), cancel.Token));
                await ProcessTests.WaitForFile(marker);
            }
            var queued = new YouTubeDownloadTool().RunAsync(ProcessTests.Child("print('OK')", TimeSpan.FromMilliseconds(300)), default);
            await Task.Delay(450);
            Assert.False(queued.IsCompleted);
            cancel.Cancel();
            foreach (var task in tasks) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(0, (await queued).ExitCode);
        }
        finally
        {
            cancel.Cancel();
            foreach (var task in tasks) { try { await task; } catch (OperationCanceledException) { } }
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SlotWaitHonorsCallerCancellationWithoutStartingChild()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        using var blockers = new CancellationTokenSource();
        var tasks = new List<Task<YouTubeDownloadToolResult>>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var marker = Path.Combine(dir, i.ToString());
                tasks.Add(new YouTubeDownloadTool().RunAsync(ProcessTests.Child($"import time; open({System.Text.Json.JsonSerializer.Serialize(marker)},'w').write('ready'); time.sleep(60)"), blockers.Token));
                await ProcessTests.WaitForFile(marker);
            }
            using var queuedCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var forbidden = Path.Combine(dir, "forbidden");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new YouTubeDownloadTool().RunAsync(ProcessTests.Child($"open({System.Text.Json.JsonSerializer.Serialize(forbidden)},'w').write('bad')"), queuedCancel.Token));
            Assert.False(File.Exists(forbidden));
        }
        finally
        {
            blockers.Cancel();
            foreach (var task in tasks) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Directory.Delete(dir, true);
        }
    }
}
