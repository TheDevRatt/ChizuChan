using ChizuChan.DTOs;
using ChizuChan.Services.Interfaces;

namespace ChizuChan.Commands;

public static class MusicSearchCommandCoordinator
{
    public static Task<StandardResponse<IReadOnlyList<LidarrAlbumDTO>>> SearchAsync(
        ulong userId,
        string query,
        IMusicSearchSessionService sessionService,
        ILidarrService lidarrService)
    {
        sessionService.ClearResults(userId);
        return lidarrService.SearchAlbumsAsync(query);
    }
}