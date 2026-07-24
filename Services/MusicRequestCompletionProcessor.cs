using System.Net;
using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCord.Rest;

namespace ChizuChan.Services;

/// <summary>
/// Advances durable music notification records. Delivery is at-least-once: a crash after Discord
/// accepts a message but before <c>Notified</c> is persisted can cause a retry. The persisted unique
/// Discord nonce reduces duplicates during Discord's nonce-deduplication window; it is not an
/// impossible exactly-once guarantee.
/// </summary>
public sealed class MusicRequestCompletionProcessor : IMusicRequestCompletionProcessor
{
    private const int MaximumAlbumsPerPoll = 10;
    private const int MaximumAttempts = 100;
    private static readonly TimeSpan HistoryClockSkewTolerance = TimeSpan.FromMinutes(2);

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

        var maximumAlbums = Math.Clamp(_options.MaxAlbumsPerPoll, 1, MaximumAlbumsPerPoll);
        var albumGroups = due
            .Where(record => record.State == MusicRequestNotificationState.Pending)
            .GroupBy(record => record.LidarrAlbumId)
            .OrderBy(group => group.Min(record => record.NextAttemptAtUtc ?? record.RequestedAtUtc))
            .ThenBy(group => group.Key)
            .Take(maximumAlbums);

        foreach (var albumGroup in albumGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subscribers = albumGroup.ToArray();
            var earliestRequestedAt = subscribers.Min(record => record.RequestedAtUtc);
            StandardResponse<LidarrAlbumCompletionDTO> response;
            try
            {
                response = await _completionReader.GetCompletionAsync(
                    albumGroup.Key,
                    earliestRequestedAt,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogSafeFailure("Lidarr completion query", exception);
                var failure = ClassifyHttpFailure(exception, "LidarrUnavailable", "LidarrPermanent", "LidarrRateLimited");
                foreach (var subscriber in subscribers)
                    await RecordFailureSafelyAsync(subscriber, failure.Category, failure.Permanent, cancellationToken);
                continue;
            }

            if (!response.Success || response.Data is null)
            {
                var failure = ClassifyStatusCode(
                    response.StatusCode,
                    "LidarrUnavailable",
                    "LidarrPermanent",
                    "LidarrRateLimited");
                foreach (var subscriber in subscribers)
                    await RecordFailureSafelyAsync(subscriber, failure.Category, failure.Permanent, cancellationToken);
                continue;
            }

            if (!response.Data.IsComplete)
            {
                var nextCheck = now.AddSeconds(Math.Clamp(_options.PendingRecheckSeconds, 10, 86_400));
                foreach (var subscriber in subscribers)
                    await ScheduleRecheckSafelyAsync(subscriber, nextCheck, cancellationToken);
                continue;
            }

            foreach (var subscriber in subscribers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var correlatedHistory = response.Data.HistoryRecordId.HasValue &&
                        response.Data.CompletedAtUtc.HasValue &&
                        response.Data.CompletedAtUtc.Value >=
                            subscriber.RequestedAtUtc.Subtract(HistoryClockSkewTolerance);
                    var observed = await _store.MarkCompletionObservedAsync(
                        subscriber.RequestId,
                        correlatedHistory ? response.Data.HistoryRecordId : null,
                        correlatedHistory ? response.Data.CompletedAtUtc!.Value : now,
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
                    await RecordFailureSafelyAsync(
                        subscriber, "StoreUnavailable", permanent: false, cancellationToken);
                }
            }
        }
    }

    private async Task ScheduleRecheckSafelyAsync(
        MusicRequestNotificationDTO record,
        DateTimeOffset nextCheck,
        CancellationToken cancellationToken)
    {
        try
        {
            await _store.SchedulePendingRecheckAsync(record.RequestId, nextCheck, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogSafeFailure("Completion recheck persistence", exception);
        }
    }

    private async Task SendObservedSafelyAsync(
        MusicRequestNotificationDTO observed,
        CancellationToken cancellationToken)
    {
        if (observed.DmChannelId == 0)
        {
            await RecordFailureSafelyAsync(
                observed, "DiscordInvalidDestination", permanent: true, cancellationToken);
            return;
        }

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
            var failure = ClassifyDiscordFailure(exception);
            await RecordFailureSafelyAsync(observed, failure.Category, failure.Permanent, cancellationToken);
        }
    }

    private async Task RecordFailureSafelyAsync(
        MusicRequestNotificationDTO record,
        string category,
        bool permanent,
        CancellationToken cancellationToken)
    {
        try
        {
            var nextAttempt = permanent
                ? (DateTimeOffset?)null
                : _timeProvider.GetUtcNow().Add(CalculateBackoff(record.AttemptCount));
            var failed = await _store.RecordAttemptFailureAsync(
                record.RequestId, category, nextAttempt, cancellationToken);
            var maximumAttempts = Math.Clamp(_options.MaxAttempts, 1, MaximumAttempts);
            if (permanent || failed.AttemptCount >= maximumAttempts)
                await _store.MarkDeadLetterAsync(record.RequestId, category, cancellationToken);
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
        var initialSeconds = Math.Clamp(_options.InitialRetryDelaySeconds, 1, 86_400);
        var maximumSeconds = Math.Clamp(_options.MaxRetryDelaySeconds, initialSeconds, 86_400);
        var exponent = Math.Clamp(previousAttempts, 0, 30);
        var seconds = Math.Min(maximumSeconds, initialSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }

    private static FailureClassification ClassifyDiscordFailure(Exception exception)
    {
        if (exception is ArgumentException or InvalidDataException)
            return new("DiscordInvalidDestination", Permanent: true);

        return ClassifyHttpFailure(
            exception,
            "DiscordUnavailable",
            "DiscordPermanent",
            "DiscordRateLimited");
    }

    private static FailureClassification ClassifyHttpFailure(
        Exception exception,
        string transientCategory,
        string permanentCategory,
        string rateLimitCategory)
    {
        var statusCode = exception switch
        {
            RestException restException => (int)restException.StatusCode,
            HttpRequestException { StatusCode: not null } httpException => (int)httpException.StatusCode.Value,
            _ => 0,
        };
        return ClassifyStatusCode(statusCode, transientCategory, permanentCategory, rateLimitCategory);
    }

    private static FailureClassification ClassifyStatusCode(
        int statusCode,
        string transientCategory,
        string permanentCategory,
        string rateLimitCategory)
    {
        if (statusCode == (int)HttpStatusCode.TooManyRequests)
            return new(rateLimitCategory, Permanent: false);
        if (statusCode is >= 400 and < 500 && statusCode != (int)HttpStatusCode.RequestTimeout)
            return new(permanentCategory, Permanent: true);
        return new(transientCategory, Permanent: false);
    }

    private void LogSafeFailure(string operation, Exception exception) =>
        _logger.LogWarning(
            "{Operation} failed ({ExceptionType}); the durable record remains safely classified.",
            operation,
            exception.GetType().Name);

    private readonly record struct FailureClassification(string Category, bool Permanent);
}
