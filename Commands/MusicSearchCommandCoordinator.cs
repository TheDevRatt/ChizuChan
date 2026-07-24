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
    private const int MaximumYouTubePages = 5;
    private static readonly ConcurrentDictionary<ulong, byte> InFlightSearches = new();

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
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(sessionService);
        ArgumentNullException.ThrowIfNull(lidarrService);
        ArgumentNullException.ThrowIfNull(youtubeService);
        ArgumentNullException.ThrowIfNull(logger);

        if (!InFlightSearches.TryAdd(userId, 0))
            throw new MusicSearchInProgressException();

        try
        {
            var lidarrTask = SearchLidarrAsync(lidarrService, query, cancellationToken);
            var youtubeTask = SearchYouTubeAsync(youtubeService, query, cancellationToken);
            await Task.WhenAll(lidarrTask, youtubeTask);

            var lidarrResponse = await lidarrTask;
            var youtubeResponse = await youtubeTask;
            if (!lidarrResponse.Success)
                logger.LogWarning("Lidarr music search provider is unavailable.");
            if (!youtubeResponse.Success)
                logger.LogWarning("YouTube music search provider is unavailable.");

            var pages = new List<MusicSearchResultPage>(MaximumLidarrPages + MaximumYouTubePages);

            if (lidarrResponse.Success && lidarrResponse.Data is not null)
            {
                pages.AddRange(lidarrResponse.Data
                    .Where(album => album is not null)
                    .Take(MaximumLidarrPages)
                    .Select(MusicSearchResultPage.FromLidarr));
            }

            if (youtubeResponse.Success && youtubeResponse.Data is not null)
            {
                var acceptedYouTubePages = 0;
                foreach (var suggestion in youtubeResponse.Data)
                {
                    if (acceptedYouTubePages >= MaximumYouTubePages)
                        break;

                    try
                    {
                        pages.Add(MusicSearchResultPage.FromYouTube(suggestion));
                        acceptedYouTubePages++;
                    }
                    catch (ArgumentException)
                    {
                        // Invalid provider suggestions are omitted; no user-controlled URL is retained.
                    }
                }
            }

            var generation = sessionService.SaveResults(
                userId,
                dmChannelId,
                query,
                pages,
                lidarrResponse.Success,
                youtubeResponse.Success);
            if (!sessionService.GetUnboundCurrent(userId, dmChannelId, generation, out var snapshot))
                throw new InvalidOperationException("The music search was superseded before it could be rendered.");

            return new MusicSearchCommandResult(generation, snapshot);
        }
        finally
        {
            InFlightSearches.TryRemove(userId, out _);
        }
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
}
