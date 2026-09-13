using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

/// <summary>Durable admission adapter for BOTH existing command entry points. The engine never sees interaction tokens.</summary>
public sealed class YouTubeLongMediaDelivery : BackgroundService, IYouTubeMusicActionHandler
{
    private readonly IYouTubeMusicActionHandler _engine;
    private readonly YouTubeLongMediaStore _store;
    private readonly IYouTubeLongMediaMessenger _messenger;
    private readonly YouTubeLongMediaDeliveryOptions _options;
    private readonly ILogger<YouTubeLongMediaDelivery> _logger;
    private volatile bool _stopping;

    public YouTubeLongMediaDelivery(IYouTubeMusicActionHandler engine, YouTubeLongMediaStore store,
        IYouTubeLongMediaMessenger messenger, IOptions<YouTubeLongMediaDeliveryOptions> options,
        ILogger<YouTubeLongMediaDelivery> logger)
    {
        _engine = engine;
        _store = store;
        _messenger = messenger;
        _options = options.Value;
        _logger = logger;
    }

    public Task<YouTubeMusicActionResult> HandleAsync(ulong userId, string canonicalVideoId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stopping) return Task.FromResult(YouTubeMusicActionResult.Failed("The download host is stopping. Please try again later."));
        if (userId == 0 || !YouTubeLongMediaStore.IsVideoId(canonicalVideoId))
            return Task.FromResult(YouTubeMusicActionResult.Failed("A valid single YouTube video is required."));
        try
        {
            var (job, error) = _store.Admit(userId, canonicalVideoId);
            if (job is null) return Task.FromResult(YouTubeMusicActionResult.Failed(error!));
            var delivery = job.DeliveredMessageId is not null ? "The result DM was already delivered." : "I'll DM the result.";
            return Task.FromResult(YouTubeMusicActionResult.Succeeded(
                $"YouTube job `{job.Id}`: {job.State}. {delivery} Use `/youtube_job` with this job ID for status, even after a restart."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("YouTube job admission storage failed ({ExceptionType}).", exception.GetType().Name);
            return Task.FromResult(YouTubeMusicActionResult.Failed("Couldn't save the YouTube job. Job storage is unavailable; no download was queued."));
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var workersCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var tasks = Enumerable.Range(0, _options.Concurrency)
            .Select(_ => DownloadLoopAsync(workersCancellation.Token)).Append(DeliveryLoopAsync(workersCancellation.Token)).ToArray();
        // If persistence or a loop fails, stop sibling loops and fault the hosted service. Never leave unobserved tasks.
        try { await Task.WhenAny(tasks); }
        finally
        {
            _stopping = true;
            await workersCancellation.CancelAsync();
            await Task.WhenAll(tasks);
        }
    }

    private async Task DownloadLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var job = _store.ClaimNext();
            if (job is null) { await Task.Delay(_options.PollIntervalMilliseconds, token); continue; }
            YouTubeLongMediaState state;
            string message;
            try
            {
                var result = await _engine.HandleAsync(job.OwnerUserId, job.VideoId, token);
                state = result.Success ? YouTubeLongMediaState.Succeeded : YouTubeLongMediaState.Failed;
                message = result.Message;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Persist despite host cancellation. Running records also recover to Interrupted after a hard crash.
                _store.Finish(job.Id, YouTubeLongMediaState.Interrupted, YouTubeLongMediaStore.InterruptedMessage);
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning("YouTube job {JobId} failed ({ExceptionType}).", job.Id, exception.GetType().Name);
                state = YouTubeLongMediaState.Failed;
                message = "Couldn't download that YouTube track right now.";
            }
            _store.Finish(job.Id, state, message);
        }
    }

    private async Task DeliveryLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var job = _store.NextDelivery(DateTimeOffset.UtcNow);
            if (job is null) { await Task.Delay(_options.PollIntervalMilliseconds, token); continue; }
            ulong? messageId = null;
            try
            {
                var sentId = await _messenger.SendAsync(job, token);
                if (sentId == 0) throw new InvalidDataException("Missing Discord message identity.");
                messageId = sentId;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _logger.LogWarning("YouTube job {JobId} DM delivery failed ({ExceptionType}); will retry.", job.Id, exception.GetType().Name);
            }
            _store.RecordDelivery(job.Id, messageId, DateTimeOffset.UtcNow);
        }
    }
}
