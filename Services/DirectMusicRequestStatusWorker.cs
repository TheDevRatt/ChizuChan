using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public sealed class DirectMusicRequestStatusWorker : BackgroundService
{
    private readonly IDirectMusicRequestStatusProcessor _processor;
    private readonly DirectMusicRequestStatusOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DirectMusicRequestStatusWorker> _logger;

    public DirectMusicRequestStatusWorker(
        IDirectMusicRequestStatusProcessor processor,
        IOptions<DirectMusicRequestStatusOptions> options,
        TimeProvider timeProvider,
        ILogger<DirectMusicRequestStatusWorker> logger)
    {
        _processor = processor;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task RunAsync(CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                    "Direct music status worker failed ({ExceptionType}).",
                    exception.GetType().Name);
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 1, 3600)),
                    _timeProvider,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
