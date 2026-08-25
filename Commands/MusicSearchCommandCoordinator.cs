using System.Collections.Concurrent;
using ChizuChan.DTOs;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChizuChan.Commands;

public sealed class MusicSearchInProgressException : InvalidOperationException
{
    public MusicSearchInProgressException()
        : base("A music search is already running for this user.")
    {
    }
}

public sealed record MusicSearchCommandResult(
    MusicSearchSessionToken Generation,
    MusicSearchSessionSnapshot Snapshot);

public static class MusicSearchCommandCoordinator
{
    private const int MaximumLidarrPages = 10;
    private const int MaximumTrackFirstLidarrPages = 5;
    private const int MaximumSoulseekPages = 5;
    private const int MaximumYouTubePages = 5;
    private static readonly ConcurrentDictionary<(IMusicSearchSessionService Session, ulong UserId), byte> InFlightSearches = new();

    // Compatibility overload for callers that explicitly use the original album-first flow.
    public static async Task<MusicSearchCommandResult> SearchAsync(
        ulong userId,
        ulong dmChannelId,
        string query,
        IMusicSearchSessionService sessionService,
        ILidarrService lidarrService,
        IYouTubeMusicSearchService youtubeService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ValidateCommon(query, sessionService, lidarrService, youtubeService, logger);
        var inFlightKey = (sessionService, userId);
        if (!InFlightSearches.TryAdd(inFlightKey, 0))
            throw new MusicSearchInProgressException();

        try
        {
            var lidarrTask = SearchLidarrAsync(lidarrService, query, cancellationToken);
            var youtubeTask = SearchYouTubeAsync(youtubeService, query, cancellationToken);
            await Task.WhenAll(lidarrTask, youtubeTask);

            var lidarrResponse = await lidarrTask;
            var youtubeResponse = await youtubeTask;
            LogUnavailable(logger, lidarrResponse.Success, youtubeResponse.Success, soulseekSuccess: null);

            var pages = new List<MusicSearchResultPage>(MaximumLidarrPages + MaximumYouTubePages);
            AddLidarrPages(pages, lidarrResponse, MaximumLidarrPages);
            AddYouTubePages(pages, youtubeResponse);

            var generation = sessionService.SaveResults(
                userId, dmChannelId, query, pages, lidarrResponse.Success, youtubeResponse.Success);
            return Snapshot(sessionService, userId, dmChannelId, generation);
        }
        finally
        {
            InFlightSearches.TryRemove(inFlightKey, out _);
        }
    }

    public static async Task<MusicSearchCommandResult> SearchAsync(
        ulong userId,
        ulong dmChannelId,
        string query,
        IMusicSearchSessionService sessionService,
        ISoulseekTrackSearchService soulseekService,
        ILidarrService lidarrService,
        IYouTubeMusicSearchService youtubeService,
        ILogger logger,
        bool includeAlbums,
        CancellationToken cancellationToken)
    {
        ValidateCommon(query, sessionService, lidarrService, youtubeService, logger);
        ArgumentNullException.ThrowIfNull(soulseekService);
        var inFlightKey = (sessionService, userId);
        if (!InFlightSearches.TryAdd(inFlightKey, 0))
            throw new MusicSearchInProgressException();

        try
        {
            var soulseekTask = SearchSoulseekAsync(soulseekService, query, cancellationToken);
            var youtubeTask = SearchYouTubeAsync(youtubeService, query, cancellationToken);
            var lidarrTask = includeAlbums
                ? SearchLidarrAsync(lidarrService, query, cancellationToken)
                : null;

            if (lidarrTask is null)
                await Task.WhenAll(soulseekTask, youtubeTask);
            else
                await Task.WhenAll(soulseekTask, youtubeTask, lidarrTask);

            var soulseekResponse = await soulseekTask;
            var youtubeResponse = await youtubeTask;
            var lidarrResponse = lidarrTask is null ? null : await lidarrTask;
            LogUnavailable(
                logger,
                includeAlbums ? lidarrResponse?.Success : null,
                youtubeResponse.Success,
                soulseekResponse.Success);

            var pages = new List<MusicSearchResultPage>(
                MaximumSoulseekPages + MaximumYouTubePages + MaximumTrackFirstLidarrPages);
            AddSoulseekPages(pages, soulseekResponse);
            AddYouTubePages(pages, youtubeResponse);
            if (lidarrResponse is not null)
                AddLidarrPages(pages, lidarrResponse, MaximumTrackFirstLidarrPages);

            var generation = sessionService.SaveResults(
                userId,
                dmChannelId,
                query,
                pages,
                includeAlbums && lidarrResponse is { Success: true },
                youtubeResponse.Success,
                soulseekResponse.Success,
                includeAlbums);
            return Snapshot(sessionService, userId, dmChannelId, generation);
        }
        finally
        {
            InFlightSearches.TryRemove(inFlightKey, out _);
        }
    }

    private static void ValidateCommon(
        string query,
        IMusicSearchSessionService sessionService,
        ILidarrService lidarrService,
        IYouTubeMusicSearchService youtubeService,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(sessionService);
        ArgumentNullException.ThrowIfNull(lidarrService);
        ArgumentNullException.ThrowIfNull(youtubeService);
        ArgumentNullException.ThrowIfNull(logger);
    }

    private static MusicSearchCommandResult Snapshot(
        IMusicSearchSessionService sessionService,
        ulong userId,
        ulong dmChannelId,
        MusicSearchSessionToken generation)
    {
        if (!sessionService.GetUnboundCurrent(userId, dmChannelId, generation, out var snapshot))
            throw new InvalidOperationException("The music search was superseded before it could be rendered.");
        return new MusicSearchCommandResult(generation, snapshot);
    }

    private static void AddLidarrPages(
        ICollection<MusicSearchResultPage> pages,
        StandardResponse<IReadOnlyList<LidarrAlbumDTO>> response,
        int limit)
    {
        if (!response.Success || response.Data is null)
            return;
        foreach (var album in response.Data.Where(album => album is not null).Take(limit))
            pages.Add(MusicSearchResultPage.FromLidarr(album));
    }

    private static void AddSoulseekPages(
        ICollection<MusicSearchResultPage> pages,
        StandardResponse<IReadOnlyList<SoulseekTrackSuggestionDTO>> response)
    {
        if (!response.Success || response.Data is null)
            return;
        var accepted = 0;
        foreach (var suggestion in response.Data)
        {
            if (accepted >= MaximumSoulseekPages)
                break;
            try
            {
                pages.Add(MusicSearchResultPage.FromSoulseek(suggestion));
                accepted++;
            }
            catch (ArgumentException)
            {
                // Invalid provider identities are never retained in a session.
            }
        }
    }

    private static void AddYouTubePages(
        ICollection<MusicSearchResultPage> pages,
        StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>> response)
    {
        if (!response.Success || response.Data is null)
            return;
        var accepted = 0;
        foreach (var suggestion in response.Data)
        {
            if (accepted >= MaximumYouTubePages)
                break;
            try
            {
                pages.Add(MusicSearchResultPage.FromYouTube(suggestion));
                accepted++;
            }
            catch (ArgumentException)
            {
                // Invalid provider identities are never retained in a session.
            }
        }
    }

    private static void LogUnavailable(
        ILogger logger,
        bool? lidarrSuccess,
        bool youtubeSuccess,
        bool? soulseekSuccess)
    {
        if (soulseekSuccess is false)
            logger.LogWarning("Soulseek music search provider is unavailable.");
        if (!youtubeSuccess)
            logger.LogWarning("YouTube Music search provider is unavailable.");
        if (lidarrSuccess is false)
            logger.LogWarning("Lidarr music search provider is unavailable.");
    }

    private static async Task<StandardResponse<IReadOnlyList<LidarrAlbumDTO>>> SearchLidarrAsync(
        ILidarrService service,
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await service.SearchAlbumsAsync(query, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StandardResponse<IReadOnlyList<LidarrAlbumDTO>>.ErrorResponse(
                "Lidarr search is unavailable.");
        }
    }

    private static async Task<StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>> SearchYouTubeAsync(
        IYouTubeMusicSearchService service,
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await service.SearchAsync(query, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>.ErrorResponse(
                "YouTube search is unavailable.");
        }
    }

    private static async Task<StandardResponse<IReadOnlyList<SoulseekTrackSuggestionDTO>>> SearchSoulseekAsync(
        ISoulseekTrackSearchService service,
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await service.SearchAsync(query, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StandardResponse<IReadOnlyList<SoulseekTrackSuggestionDTO>>.ErrorResponse(
                "Soulseek search is unavailable.");
        }
    }
}
