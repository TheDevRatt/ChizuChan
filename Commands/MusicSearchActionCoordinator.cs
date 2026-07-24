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
        Func<CancellationToken, Task> acknowledgeAsync,
        Func<MusicSearchActionResult, CancellationToken, Task> commitAsync,
        CancellationToken cancellationToken = default);
}

public sealed class MusicSearchActionCoordinator : IMusicSearchActionCoordinator
{
    private const string ExpiredMessage =
        "This music search has expired or was superseded. Run `/music_search` again.";

    private readonly IMusicSearchSessionService _sessionService;
    private readonly IMusicRequestAccessService _accessService;
    private readonly ILidarrService _lidarrService;
    private readonly IYouTubeMusicActionHandler _youTubeHandler;
    private readonly ILogger<MusicSearchActionCoordinator> _logger;

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
        _youTubeHandler = youTubeHandler;
        _logger = logger;
    }

    public Task<MusicSearchActionResult> ExecuteAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        CancellationToken cancellationToken = default)
    {
        var found = _sessionService.TryCaptureCurrentSelection(
            userId, dmChannelId, sourceMessageId, out var selection);
        return ExecuteCapturedAndCommitAsync(
            found ? selection : null,
            static _ => Task.CompletedTask,
            static (_, _) => Task.CompletedTask,
            cancellationToken);
    }

    // This method intentionally is not async: capture happens synchronously when the
    // interaction handler calls it, before acknowledgement can yield to another interaction.
    public Task<MusicSearchActionResult> ExecuteAndCommitAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        Func<CancellationToken, Task> acknowledgeAsync,
        Func<MusicSearchActionResult, CancellationToken, Task> commitAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgeAsync);
        ArgumentNullException.ThrowIfNull(commitAsync);

        var found = _sessionService.TryCaptureCurrentSelection(
            userId, dmChannelId, sourceMessageId, out var selection);
        return ExecuteCapturedAndCommitAsync(
            found ? selection : null,
            acknowledgeAsync,
            commitAsync,
            cancellationToken);
    }

    private async Task<MusicSearchActionResult> ExecuteCapturedAndCommitAsync(
        MusicSearchSelectionSnapshot? selection,
        Func<CancellationToken, Task> acknowledgeAsync,
        Func<MusicSearchActionResult, CancellationToken, Task> commitAsync,
        CancellationToken cancellationToken)
    {
        await acknowledgeAsync(cancellationToken);

        var result = selection is null
            ? MusicSearchActionResult.Failed(ExpiredMessage)
            : await ExecuteCoreAsync(selection, cancellationToken);
        await commitAsync(result, cancellationToken);
        return result;
    }

    private async Task<MusicSearchActionResult> ExecuteCoreAsync(
        MusicSearchSelectionSnapshot selection,
        CancellationToken cancellationToken)
    {
        var page = selection.Page;
        if (page.Kind == MusicSearchResultKind.YouTubeTrack)
        {
            var track = page.YouTubeTrack
                ?? throw new InvalidOperationException("A YouTube page requires track data.");
            var youTubeResponse = await _youTubeHandler.HandleAsync(
                selection.OwnerUserId, track.VideoId, cancellationToken);
            return new MusicSearchActionResult(youTubeResponse.Success, youTubeResponse.Message);
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
