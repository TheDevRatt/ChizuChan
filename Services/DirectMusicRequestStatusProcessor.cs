using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public sealed class DirectMusicRequestStatusProcessor : IDirectMusicRequestStatusProcessor
{
    private const int MaximumRequestsPerPoll = 100;
    private readonly IDirectMusicRequestStatusStore _store;
    private readonly ISoulseekDownloadStatusReader _soulseekReader;
    private readonly ISoulseekTrackSearchService _soulseekDispatcher;
    private readonly IPlexMusicReadinessReader _plexReader;
    private readonly DirectMusicRequestStatusOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DirectMusicRequestStatusProcessor> _logger;

    public DirectMusicRequestStatusProcessor(
        IDirectMusicRequestStatusStore store,
        ISoulseekDownloadStatusReader soulseekReader,
        ISoulseekTrackSearchService soulseekDispatcher,
        IPlexMusicReadinessReader plexReader,
        IOptions<DirectMusicRequestStatusOptions> options,
        TimeProvider timeProvider,
        ILogger<DirectMusicRequestStatusProcessor> logger)
    {
        _store = store;
        _soulseekReader = soulseekReader;
        _soulseekDispatcher = soulseekDispatcher;
        _plexReader = plexReader;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        var maximum = Math.Clamp(_options.MaxRequestsPerPoll, 1, MaximumRequestsPerPoll);
        var active = (await _store.GetActiveAsync(cancellationToken))
            .OrderBy(item => item.UpdatedAtUtc)
            .ThenBy(item => item.RequestId)
            .Take(maximum)
            .ToArray();

        foreach (var request in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (request.State == DirectMusicRequestState.DownloadedWaitingForPlex)
                {
                    await ProbePlexAsync(request, persistWaitingState: false, cancellationToken);
                    continue;
                }

                await ProbeSoulseekAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Direct music status probe failed ({ExceptionType}).",
                    exception.GetType().Name);
            }
        }
    }

    private async Task ProbeSoulseekAsync(
        DirectMusicRequestStatusDTO request,
        CancellationToken cancellationToken)
    {
        var response = await _soulseekReader.GetBatchStatusAsync(request.BatchId, cancellationToken);
        if (!response.Success || response.Data is null)
            return;

        var status = response.Data;
        if (status.State == SoulseekDownloadBatchState.Failed && status.FailureCategory == "TransferMissing")
        {
            if (request.State == DirectMusicRequestState.DispatchPending)
            {
                var graceSeconds = Math.Clamp(_options.DispatchGraceSeconds, 5, 3600);
                if (_timeProvider.GetUtcNow() < request.UpdatedAtUtc.AddSeconds(graceSeconds))
                    return;
                await ReplayDispatchAsync(request, cancellationToken);
                return;
            }
            await _store.UpdateAsync(request with
            {
                State = DirectMusicRequestState.Failed,
                FailureCategory = "TransferMissing",
            }, cancellationToken);
            return;
        }
        if (!status.TransferId.HasValue ||
            (request.TransferId.HasValue && request.TransferId.Value != status.TransferId.Value) ||
            status.Username != request.Username ||
            status.Filename != request.RemoteFilename ||
            status.Size != request.ExpectedSize ||
            status.BytesTransferred < 0 ||
            status.BytesTransferred > request.ExpectedSize)
        {
            await _store.UpdateAsync(request with
            {
                State = DirectMusicRequestState.Failed,
                FailureCategory = "TransferMismatch",
            }, cancellationToken);
            return;
        }

        var correlated = request with
        {
            TransferId = status.TransferId ?? request.TransferId,
            BytesTransferred = Math.Max(request.BytesTransferred, status.BytesTransferred),
        };
        switch (status.State)
        {
            case SoulseekDownloadBatchState.Queued:
                if (correlated.TransferId != request.TransferId ||
                    correlated.BytesTransferred != request.BytesTransferred)
                {
                    await _store.UpdateAsync(correlated with
                    {
                        State = DirectMusicRequestState.Queued,
                    }, cancellationToken);
                }
                return;
            case SoulseekDownloadBatchState.Downloading:
                await _store.UpdateAsync(correlated with
                {
                    State = DirectMusicRequestState.Downloading,
                }, cancellationToken);
                return;
            case SoulseekDownloadBatchState.Succeeded:
                var downloaded = await _store.UpdateAsync(correlated with
                {
                    State = DirectMusicRequestState.DownloadedWaitingForPlex,
                    BytesTransferred = request.ExpectedSize,
                }, cancellationToken);
                await ProbePlexAsync(downloaded, persistWaitingState: false, cancellationToken);
                return;
            case SoulseekDownloadBatchState.Failed:
                await _store.UpdateAsync(correlated with
                {
                    State = DirectMusicRequestState.Failed,
                    FailureCategory = SafeFailureCategory(status.FailureCategory),
                }, cancellationToken);
                return;
            default:
                throw new InvalidDataException("The Soulseek batch state is invalid.");
        }
    }

    private async Task ReplayDispatchAsync(
        DirectMusicRequestStatusDTO request,
        CancellationToken cancellationToken)
    {
        var track = new SoulseekTrackSearchResult(
            request.SearchId,
            request.Username,
            request.RemoteFilename,
            request.ExpectedSize,
            request.TrackTitle,
            request.ArtistName,
            request.ExpectedDuration,
            string.Empty,
            false,
            0,
            0);
        var response = await _soulseekDispatcher.QueueDownloadAsync(track, request.BatchId, cancellationToken);
        if (response.Success && response.Data is not null && ReceiptMatches(response.Data, request))
        {
            await _store.UpdateAsync(request with
            {
                State = DirectMusicRequestState.Queued,
                TransferId = response.Data.TransferId,
            }, cancellationToken);
            return;
        }

        if (IsDefinitiveQueueRejection(response.StatusCode))
        {
            await _store.UpdateAsync(request with
            {
                State = DirectMusicRequestState.Failed,
                FailureCategory = "QueueRejected",
            }, cancellationToken);
            return;
        }

        await _store.UpdateAsync(request, cancellationToken);
    }

    private static bool ReceiptMatches(
        SoulseekDownloadReceiptDTO receipt,
        DirectMusicRequestStatusDTO request) =>
        receipt.BatchId == request.BatchId &&
        receipt.TransferId != Guid.Empty &&
        receipt.Username == request.Username &&
        receipt.Filename == request.RemoteFilename &&
        receipt.Size == request.ExpectedSize;

    private static bool IsDefinitiveQueueRejection(int statusCode) =>
        statusCode is 400 or 403 or 404;

    private async Task ProbePlexAsync(
        DirectMusicRequestStatusDTO request,
        bool persistWaitingState,
        CancellationToken cancellationToken)
    {
        var response = await _plexReader.FindTrackAsync(request, cancellationToken);
        if (!response.Success || response.Data is null)
            return;

        if (response.Data is { IsReady: true } ready &&
            !string.IsNullOrWhiteSpace(ready.MediaIdentity))
        {
            await _store.UpdateAsync(request with
            {
                State = DirectMusicRequestState.ReadyInPlexamp,
                BytesTransferred = request.ExpectedSize,
                PlexMediaPath = ready.MediaIdentity,
            }, cancellationToken);
            return;
        }

        var timeoutHours = Math.Clamp(_options.PlexReadyTimeoutHours, 1, 24 * 30);
        if (!persistWaitingState &&
            _timeProvider.GetUtcNow() >= request.UpdatedAtUtc.AddHours(timeoutHours))
        {
            await _store.UpdateAsync(request with
            {
                State = DirectMusicRequestState.Failed,
                BytesTransferred = request.ExpectedSize,
                FailureCategory = "PlexIndexTimeout",
            }, cancellationToken);
            return;
        }

        if (persistWaitingState ||
            request.State != DirectMusicRequestState.DownloadedWaitingForPlex ||
            request.BytesTransferred != request.ExpectedSize)
        {
            await _store.UpdateAsync(request with
            {
                State = DirectMusicRequestState.DownloadedWaitingForPlex,
                BytesTransferred = request.ExpectedSize,
            }, cancellationToken);
        }
    }

    private static string SafeFailureCategory(string? category) => category switch
    {
        "TransferCancelled" => "TransferCancelled",
        "TransferTimedOut" => "TransferTimedOut",
        "TransferMissing" => "TransferMissing",
        "TransferMismatch" => "TransferMismatch",
        _ => "TransferFailed",
    };
}
