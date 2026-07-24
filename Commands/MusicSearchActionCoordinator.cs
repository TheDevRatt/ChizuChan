using ChizuChan.DTOs;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
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
        CancellationToken cancellationToken = default);

    Task<MusicSearchActionResult> ExecuteAndCommitAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        Func<MusicSearchActionResult, CancellationToken, Task> commitAsync,
        CancellationToken cancellationToken = default);
}

public sealed class MusicSearchActionCoordinator : IMusicSearchActionCoordinator
{
    private readonly IMusicSearchSessionService _sessionService;
    private readonly IMusicRequestAccessService _accessService;
    private readonly ILidarrService _lidarrService;
    private readonly IYouTubeMusicActionHandler _youTubeHandler;
    private readonly ILogger<MusicSearchActionCoordinator> _logger;
    private readonly IMusicSearchInteractionGate _interactionGate;

    public MusicSearchActionCoordinator(
        IMusicSearchSessionService sessionService,
        IMusicRequestAccessService accessService,
        ILidarrService lidarrService,
        IYouTubeMusicActionHandler youTubeHandler,
        ILogger<MusicSearchActionCoordinator> logger)
        : this(
            sessionService,
            accessService,
            lidarrService,
            youTubeHandler,
            logger,
            new MusicSearchInteractionGate())
    {
    }

    public MusicSearchActionCoordinator(
        IMusicSearchSessionService sessionService,
        IMusicRequestAccessService accessService,
        ILidarrService lidarrService,
        IYouTubeMusicActionHandler youTubeHandler,
        ILogger<MusicSearchActionCoordinator> logger,
        IMusicSearchInteractionGate interactionGate)
    {
        _sessionService = sessionService;
        _accessService = accessService;
        _lidarrService = lidarrService;
        _youTubeHandler = youTubeHandler;
        _logger = logger;
        _interactionGate = interactionGate;
    }

    public Task<MusicSearchActionResult> ExecuteAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        CancellationToken cancellationToken = default) =>
        ExecuteAndCommitAsync(
            userId,
            dmChannelId,
            sourceMessageId,
            static (_, _) => Task.CompletedTask,
            cancellationToken);

    public async Task<MusicSearchActionResult> ExecuteAndCommitAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        Func<MusicSearchActionResult, CancellationToken, Task> commitAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commitAsync);

        await using var lease = await _interactionGate.EnterAsync(
            userId, dmChannelId, sourceMessageId, cancellationToken);
        var result = await ExecuteCoreAsync(userId, dmChannelId, sourceMessageId, cancellationToken);
        await commitAsync(result, cancellationToken);
        return result;
    }

    private async Task<MusicSearchActionResult> ExecuteCoreAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        CancellationToken cancellationToken)
    {
        if (!_sessionService.GetCurrent(userId, dmChannelId, sourceMessageId, out var session) ||
            session.CurrentPage is not { } page)
        {
            return MusicSearchActionResult.Failed(
                "This music search has expired or was superseded. Run `/music_search` again.");
        }

        if (page.Kind == MusicSearchResultKind.YouTubeTrack)
        {
            var track = page.YouTubeTrack
                ?? throw new InvalidOperationException("A YouTube page requires track data.");
            var youTubeResponse = await _youTubeHandler.HandleAsync(userId, track.VideoId, cancellationToken);
            return new MusicSearchActionResult(youTubeResponse.Success, youTubeResponse.Message);
        }

        var access = _accessService.CheckAccess(userId, MusicRequestOperation.Request);
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
            response = await _lidarrService.RequestAlbumAsync(selectedAlbum);
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

        var label = $"**{Limit(response.Data.ArtistName, 70)} — {Limit(response.Data.Title, 70)}**";
        return response.Data.AlreadyExists
            ? MusicSearchActionResult.Succeeded($"{label} is already in Lidarr.")
            : MusicSearchActionResult.Succeeded($"Queued {label} in Lidarr.");
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";
}
