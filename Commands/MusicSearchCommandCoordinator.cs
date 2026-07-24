using System.Collections.Concurrent;
using ChizuChan.DTOs;
using ChizuChan.Services.Interfaces;

namespace ChizuChan.Commands;

public sealed class MusicSearchInProgressException : InvalidOperationException
{
    public MusicSearchInProgressException()
        : base("A music search is already running for this user.")
    {
    }
}

public static class MusicSearchCommandCoordinator
{
    private static readonly ConcurrentDictionary<ulong, byte> InFlightSearches = new();

    public static async Task<MusicSearchResultsDTO> SearchAsync(
        ulong userId,
        string query,
        IMusicSearchSessionService sessionService,
        ILidarrService lidarrService,
        IYouTubeMusicSearchService youtubeService,
        CancellationToken cancellationToken)
    {
        if (!InFlightSearches.TryAdd(userId, 0))
            throw new MusicSearchInProgressException();

        try
        {
            sessionService.ClearResults(userId);
            var lidarrTask = SearchLidarrAsync(lidarrService, query, cancellationToken);
            var youtubeTask = SearchYouTubeAsync(youtubeService, query, cancellationToken);
            await Task.WhenAll(lidarrTask, youtubeTask);

            var lidarrResponse = await lidarrTask;
            var youtubeResponse = await youtubeTask;
            var albums = lidarrResponse.Success
                ? lidarrResponse.Data?.Take(10).ToArray() ?? []
                : [];
            var youtubeTracks = youtubeResponse.Success
                ? youtubeResponse.Data?.ToArray() ?? []
                : [];

            sessionService.SaveResults(userId, albums);
            return new MusicSearchResultsDTO
            {
                Albums = albums,
                YouTubeTracks = youtubeTracks,
                LidarrAvailable = lidarrResponse.Success,
                YouTubeAvailable = youtubeResponse.Success,
            };
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
        catch
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
        catch
        {
            return StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>.ErrorResponse(
                "YouTube search is unavailable.");
        }
    }
}
