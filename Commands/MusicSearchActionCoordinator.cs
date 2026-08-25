using System.Text;
using ChizuChan.DTOs;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChizuChan.Commands;

public readonly record struct MusicSearchActionResult(bool Success, string Message)
{
    public static MusicSearchActionResult Succeeded(string message) => new(true, message);
    public static MusicSearchActionResult Failed(string message) => new(false, message);
}

public interface IMusicSearchActionCoordinator
{
    Task<MusicSearchActionResult> ExecuteAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        string actionTokenSegment,
        int index,
        CancellationToken cancellationToken = default);

    Task<MusicSearchActionResult> ExecuteAndCommitAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        string actionTokenSegment,
        int index,
        Func<CancellationToken, Task> acknowledgeAsync,
        Func<MusicSearchActionResult, CancellationToken, Task> commitAsync,
        CancellationToken cancellationToken = default);
}

public sealed class MusicSearchActionCoordinator : IMusicSearchActionCoordinator
{
    private const string ExpiredMessage =
        "This music search has expired or was superseded. Run `/music_search` again.";
    private const string AlreadyProcessingMessage =
        "This selection is already being processed. Please wait for it to finish.";

    private readonly IMusicSearchSessionService _sessionService;
    private readonly IMusicRequestAccessService _accessService;
    private readonly ILidarrService _lidarrService;
    private readonly ISoulseekTrackSearchService? _soulseekService;
    private readonly IYouTubeMusicActionHandler _youTubeHandler;
    private readonly IMusicRequestNotificationStore? _notificationStore;
    private readonly IDirectMusicRequestStatusStore? _directStatusStore = null;
    private readonly ILogger<MusicSearchActionCoordinator> _logger;

    [ActivatorUtilitiesConstructor]
    public MusicSearchActionCoordinator(
        IMusicSearchSessionService sessionService,
        IMusicRequestAccessService accessService,
        ILidarrService lidarrService,
        ISoulseekTrackSearchService soulseekService,
        IYouTubeMusicActionHandler youTubeHandler,
        IMusicRequestNotificationStore notificationStore,
        IDirectMusicRequestStatusStore directStatusStore,
        ILogger<MusicSearchActionCoordinator> logger)
    {
        _sessionService = sessionService;
        _accessService = accessService;
        _lidarrService = lidarrService;
        _soulseekService = soulseekService;
        _youTubeHandler = youTubeHandler;
        _notificationStore = notificationStore;
        _directStatusStore = directStatusStore;
        _logger = logger;
    }

    // Compatibility constructors for narrow unit-test and non-DI callers.
    public MusicSearchActionCoordinator(
        IMusicSearchSessionService sessionService,
        IMusicRequestAccessService accessService,
        ILidarrService lidarrService,
        IYouTubeMusicActionHandler youTubeHandler,
        IMusicRequestNotificationStore notificationStore,
        ILogger<MusicSearchActionCoordinator> logger)
    {
        _sessionService = sessionService;
        _accessService = accessService;
        _lidarrService = lidarrService;
        _soulseekService = null;
        _youTubeHandler = youTubeHandler;
        _notificationStore = notificationStore;
        _logger = logger;
    }

    public MusicSearchActionCoordinator(
        IMusicSearchSessionService sessionService,
        IMusicRequestAccessService accessService,
        ILidarrService lidarrService,
        ISoulseekTrackSearchService soulseekService,
        IYouTubeMusicActionHandler youTubeHandler,
        ILogger<MusicSearchActionCoordinator> logger)
    {
        _sessionService = sessionService;
        _accessService = accessService;
        _lidarrService = lidarrService;
        _soulseekService = soulseekService;
        _youTubeHandler = youTubeHandler;
        _notificationStore = null;
        _logger = logger;
    }

    public MusicSearchActionCoordinator(
        IMusicSearchSessionService sessionService,
        IMusicRequestAccessService accessService,
        ILidarrService lidarrService,
        IYouTubeMusicActionHandler youTubeHandler,
        ILogger<MusicSearchActionCoordinator> logger)
    {
        _sessionService = sessionService;
        _accessService = accessService;
        _lidarrService = lidarrService;
        _soulseekService = null;
        _youTubeHandler = youTubeHandler;
        _notificationStore = null;
        _logger = logger;
    }

    public Task<MusicSearchActionResult> ExecuteAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        string actionTokenSegment,
        int index,
        CancellationToken cancellationToken = default)
    {
        var found = _sessionService.TryClaimRenderedSelection(
            userId,
            dmChannelId,
            sourceMessageId,
            actionTokenSegment,
            index,
            out var claim);
        return ExecuteClaimedAndCommitAsync(
            found ? claim : null,
            claim.Failure,
            static _ => Task.CompletedTask,
            static (_, _) => Task.CompletedTask,
            cancellationToken);
    }

    // This method intentionally is not async: the rendered token/index is claimed synchronously
    // when the interaction handler calls it, before acknowledgement can yield.
    public Task<MusicSearchActionResult> ExecuteAndCommitAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        string actionTokenSegment,
        int index,
        Func<CancellationToken, Task> acknowledgeAsync,
        Func<MusicSearchActionResult, CancellationToken, Task> commitAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgeAsync);
        ArgumentNullException.ThrowIfNull(commitAsync);

        var found = _sessionService.TryClaimRenderedSelection(
            userId,
            dmChannelId,
            sourceMessageId,
            actionTokenSegment,
            index,
            out var claim);
        return ExecuteClaimedAndCommitAsync(
            found ? claim : null,
            claim.Failure,
            acknowledgeAsync,
            commitAsync,
            cancellationToken);
    }

    private async Task<MusicSearchActionResult> ExecuteClaimedAndCommitAsync(
        MusicSearchSelectionClaim? claim,
        MusicSearchSelectionClaimFailure claimFailure,
        Func<CancellationToken, Task> acknowledgeAsync,
        Func<MusicSearchActionResult, CancellationToken, Task> commitAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            await acknowledgeAsync(cancellationToken);

            var result = claim is null
                ? MusicSearchActionResult.Failed(claimFailure == MusicSearchSelectionClaimFailure.AlreadyProcessing
                    ? AlreadyProcessingMessage
                    : ExpiredMessage)
                : await ExecuteCoreAsync(claim.Selection, cancellationToken);
            await commitAsync(result, cancellationToken);
            return result;
        }
        finally
        {
            if (claim is not null)
                _sessionService.ReleaseSelectionClaim(claim);
        }
    }

    private async Task<MusicSearchActionResult> ExecuteCoreAsync(
        MusicSearchSelectionSnapshot selection,
        CancellationToken cancellationToken)
    {
        var page = selection.Page;
        if (page.Kind == MusicSearchResultKind.SoulseekTrack)
        {
            var downloadAccess = _accessService.CheckAccess(selection.OwnerUserId, MusicRequestOperation.Download);
            if (downloadAccess.Status == MusicRequestAccessStatus.Unauthorized)
                return MusicSearchActionResult.Failed("You don't have permission to use music requests.");
            if (downloadAccess.Status == MusicRequestAccessStatus.RateLimited)
            {
                var seconds = Math.Max(1, (int)Math.Ceiling(downloadAccess.RetryAfter.TotalSeconds));
                return MusicSearchActionResult.Failed($"Please wait {seconds}s before requesting again.");
            }

            if (_soulseekService is null)
                return MusicSearchActionResult.Failed("Soulseek downloads are unavailable right now.");
            var track = page.SoulseekTrack
                ?? throw new InvalidOperationException("A Soulseek page requires track data.");
            var artist = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown artist" : track.Artist;
            var directQueuedMessage =
                $"Queued **{EscapeDiscordText(artist, 70)} — {EscapeDiscordText(track.Title, 70)}** from Soulseek. " +
                "The organizer imports it into Plex after the transfer completes.";
            DirectMusicRequestStatusDTO? persistedIntent = null;
            try
            {
                StandardResponse<SoulseekDownloadReceiptDTO> soulseekResponse;
                if (_directStatusStore is null)
                {
                    soulseekResponse = await _soulseekService.QueueDownloadAsync(track, cancellationToken);
                }
                else
                {
                    var batchId = Guid.NewGuid();
                    persistedIntent = await _directStatusStore.AddAsync(new DirectMusicRequestStatusDTO
                    {
                        BatchId = batchId,
                        DiscordUserId = selection.OwnerUserId,
                        DmChannelId = selection.DmChannelId,
                        Username = track.Username,
                        RemoteFilename = track.Filename,
                        ArtistName = artist,
                        TrackTitle = track.Title,
                        ExpectedSize = track.Size,
                        ExpectedDuration = track.Duration,
                        State = DirectMusicRequestState.DispatchPending,
                    }, cancellationToken);
                    soulseekResponse = await _soulseekService.QueueDownloadAsync(
                        track, batchId, cancellationToken);
                }

                if (!soulseekResponse.Success || soulseekResponse.Data is null)
                {
                    await MarkDirectQueueFailureSafelyAsync(persistedIntent, cancellationToken);
                    return MusicSearchActionResult.Failed("Couldn't queue that Soulseek track right now.");
                }

                if (_directStatusStore is null)
                    return MusicSearchActionResult.Succeeded(directQueuedMessage);

                return MusicSearchActionResult.Succeeded(
                    directQueuedMessage + " Check `/music_status` for progress.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await MarkDirectQueueFailureSafelyAsync(persistedIntent, cancellationToken);
                _logger.LogWarning(
                    "Soulseek music action provider failed ({ExceptionType}).",
                    exception.GetType().Name);
                return MusicSearchActionResult.Failed("Couldn't queue that Soulseek track right now.");
            }
        }

        if (page.Kind == MusicSearchResultKind.YouTubeTrack)
        {
            var downloadAccess = _accessService.CheckAccess(selection.OwnerUserId, MusicRequestOperation.Download);
            if (downloadAccess.Status == MusicRequestAccessStatus.Unauthorized)
                return MusicSearchActionResult.Failed("You don't have permission to use music requests.");
            if (downloadAccess.Status == MusicRequestAccessStatus.RateLimited)
            {
                var seconds = Math.Max(1, (int)Math.Ceiling(downloadAccess.RetryAfter.TotalSeconds));
                return MusicSearchActionResult.Failed($"Please wait {seconds}s before requesting again.");
            }

            var track = page.YouTubeTrack
                ?? throw new InvalidOperationException("A YouTube page requires track data.");
            try
            {
                var youTubeResponse = await _youTubeHandler.HandleAsync(
                    selection.OwnerUserId, track.VideoId, cancellationToken);
                return new MusicSearchActionResult(youTubeResponse.Success, youTubeResponse.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "YouTube music action provider failed ({ExceptionType}).",
                    exception.GetType().Name);
                return MusicSearchActionResult.Failed("Couldn't download that YouTube track right now.");
            }
        }

        var access = _accessService.CheckAccess(selection.OwnerUserId, MusicRequestOperation.Request);
        if (access.Status == MusicRequestAccessStatus.Unauthorized)
            return MusicSearchActionResult.Failed("You don't have permission to use music requests.");
        if (access.Status == MusicRequestAccessStatus.RateLimited)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(access.RetryAfter.TotalSeconds));
            return MusicSearchActionResult.Failed($"Please wait {seconds}s before requesting again.");
        }

        var selectedAlbum = page.LidarrAlbum?.ToDto()
            ?? throw new InvalidOperationException("A Lidarr page requires album data.");
        StandardResponse<LidarrAlbumRequestResultDTO> response;
        try
        {
            response = await _lidarrService.RequestAlbumAsync(selectedAlbum, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Lidarr music request provider failed ({ExceptionType}).",
                exception.GetType().Name);
            return MusicSearchActionResult.Failed("Couldn't request that release right now.");
        }

        if (!response.Success || response.Data is null)
            return MusicSearchActionResult.Failed("Couldn't request that release right now.");

        var label =
            $"**{EscapeDiscordText(response.Data.ArtistName, 70)} — {EscapeDiscordText(response.Data.Title, 70)}**";
        var queuedMessage = response.Data.AlreadyExists
            ? $"{label} is already present in Lidarr."
            : $"Queued {label} in Lidarr.";

        // Older direct callers do not have notification persistence. Production DI always uses
        // the constructor with the store.
        if (_notificationStore is null)
            return MusicSearchActionResult.Succeeded(queuedMessage);

        if (response.Data.AlbumId <= 0 || string.IsNullOrWhiteSpace(response.Data.ForeignAlbumId))
            return MusicSearchActionResult.Failed("Lidarr did not return an authoritative album identity.");

        try
        {
            await _notificationStore.AddOrGetAsync(new MusicRequestNotificationDTO
            {
                LidarrAlbumId = response.Data.AlbumId,
                ForeignAlbumId = response.Data.ForeignAlbumId,
                DiscordUserId = selection.OwnerUserId,
                DmChannelId = selection.DmChannelId,
                ArtistName = response.Data.ArtistName,
                AlbumTitle = response.Data.Title,
                State = MusicRequestNotificationState.Pending,
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Music completion subscription could not be persisted ({ExceptionType}).",
                exception.GetType().Name);
            return MusicSearchActionResult.Succeeded(
                $"{queuedMessage} I couldn't register the completion alert, so please check Plex/Plexamp later.");
        }

        return MusicSearchActionResult.Succeeded(
            $"{queuedMessage} Chizu will DM you here when it's ready in Plex/Plexamp.");
    }

    private async Task MarkDirectQueueFailureSafelyAsync(
        DirectMusicRequestStatusDTO? intent,
        CancellationToken cancellationToken)
    {
        if (intent is null || _directStatusStore is null)
            return;
        try
        {
            await _directStatusStore.UpdateAsync(intent with
            {
                State = DirectMusicRequestState.Failed,
                FailureCategory = "QueueRejected",
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Direct queue failure could not be persisted ({ExceptionType}).",
                exception.GetType().Name);
        }
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";

    private static string EscapeDiscordText(string value, int maximumLength)
    {
        var builder = new StringBuilder(maximumLength * 2);
        foreach (var character in Limit(value.Trim(), maximumLength))
        {
            if (char.IsControl(character))
            {
                builder.Append(' ');
                continue;
            }
            if (character == '@')
            {
                builder.Append("@\u200B");
                continue;
            }
            if (character is '\\' or '`' or '*' or '_' or '~' or '|' or '[' or ']' or '(' or ')' or '<' or '>')
                builder.Append('\\');
            builder.Append(character);
        }
        return builder.ToString();
    }
}
