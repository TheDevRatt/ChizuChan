using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

/// <summary>
/// Advances durable music notification records. Delivery is at-least-once: a crash after Discord
/// accepts a message but before <c>Notified</c> is persisted can cause a retry. The persisted unique
/// Discord nonce reduces duplicates during Discord's nonce-deduplication window; it is not an
/// impossible exactly-once guarantee.
/// </summary>
public sealed class MusicRequestCompletionProcessor : IMusicRequestCompletionProcessor
{
    private readonly IMusicRequestNotificationStore _store;
    private readonly ILidarrCompletionReader _completionReader;
    private readonly IDiscordDmSender _dmSender;
    private readonly MusicRequestNotificationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MusicRequestCompletionProcessor> _logger;

    public MusicRequestCompletionProcessor(
        IMusicRequestNotificationStore store,
        ILidarrCompletionReader completionReader,
        IDiscordDmSender dmSender,
        IOptions<MusicRequestNotificationOptions> options,
        TimeProvider timeProvider,
        ILogger<MusicRequestCompletionProcessor> logger)
    {
        _store = store;
        _completionReader = completionReader;
        _dmSender = dmSender;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var active = await _store.GetActiveAsync(cancellationToken);
        var due = active
            .Where(record => !record.NextAttemptAtUtc.HasValue || record.NextAttemptAtUtc <= now)
            .ToArray();

        foreach (var observed in due.Where(record =>
                     record.State == MusicRequestNotificationState.CompletionObserved))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendObservedSafelyAsync(observed, cancellationToken);
        }

        foreach (var albumGroup in due
                     .Where(record => record.State == MusicRequestNotificationState.Pending)
                     .GroupBy(record => record.LidarrAlbumId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            StandardResponse<LidarrAlbumCompletionDTO> response;
            try
            {
                response = await _completionReader.GetCompletionAsync(albumGroup.Key, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogSafeFailure("Lidarr completion query", exception);
                foreach (var subscriber in albumGroup)
                    await RecordFailureSafelyAsync(subscriber, "LidarrUnavailable", cancellationToken);
                continue;
            }

            if (!response.Success || response.Data is null)
            {
                foreach (var subscriber in albumGroup)
                    await RecordFailureSafelyAsync(subscriber, "LidarrUnavailable", cancellationToken);
                continue;
            }

            if (!response.Data.IsComplete)
                continue;

            var observedAt = response.Data.CompletedAtUtc ?? now;
            foreach (var subscriber in albumGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var observed = await _store.MarkCompletionObservedAsync(
                        subscriber.RequestId,
                        response.Data.HistoryRecordId,
                        observedAt,
                        cancellationToken);
                    await SendObservedSafelyAsync(observed, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogSafeFailure("Completion persistence", exception);
                    await RecordFailureSafelyAsync(subscriber, "StoreUnavailable", cancellationToken);
                }
            }
        }
    }

    private async Task SendObservedSafelyAsync(
        MusicRequestNotificationDTO observed,
        CancellationToken cancellationToken)
    {
        try
        {
            var messageId = await _dmSender.SendCompletionAsync(observed, cancellationToken);
            await _store.MarkNotifiedAsync(observed.RequestId, messageId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogSafeFailure("Discord completion notification", exception);
            await RecordFailureSafelyAsync(observed, "DiscordUnavailable", cancellationToken);
        }
    }

    private async Task RecordFailureSafelyAsync(
        MusicRequestNotificationDTO record,
        string category,
        CancellationToken cancellationToken)
    {
        try
        {
            var nextAttempt = _timeProvider.GetUtcNow().Add(CalculateBackoff(record.AttemptCount));
            await _store.RecordAttemptFailureAsync(
                record.RequestId, category, nextAttempt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogSafeFailure("Notification failure persistence", exception);
        }
    }

    private TimeSpan CalculateBackoff(int previousAttempts)
    {
        var initialSeconds = Math.Max(1, _options.InitialRetryDelaySeconds);
        var maximumSeconds = Math.Max(initialSeconds, _options.MaxRetryDelaySeconds);
        var exponent = Math.Clamp(previousAttempts, 0, 30);
        var seconds = Math.Min(maximumSeconds, initialSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }

    private void LogSafeFailure(string operation, Exception exception) =>
        _logger.LogWarning(
            "{Operation} failed ({ExceptionType}); the durable record remains retryable.",
            operation,
            exception.GetType().Name);
}
