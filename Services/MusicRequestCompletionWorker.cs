using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public sealed class MusicRequestCompletionWorker : BackgroundService
{
    private readonly IMusicRequestCompletionProcessor _processor;
    private readonly MusicRequestNotificationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MusicRequestCompletionWorker> _logger;

    public MusicRequestCompletionWorker(
        IMusicRequestCompletionProcessor processor,
        IOptions<MusicRequestNotificationOptions> options,
        TimeProvider timeProvider,
        ILogger<MusicRequestCompletionWorker> logger)
    {
        _processor = processor;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _processor.ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Music completion polling cycle failed ({ExceptionType}); polling will continue.",
                    exception.GetType().Name);
            }

            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
