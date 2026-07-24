using ChizuChan.DTOs;

namespace ChizuChan.Services.Interfaces;

public interface ILidarrService
{
    Task<StandardResponse<IReadOnlyList<LidarrAlbumDTO>>> SearchAlbumsAsync(
        string query,
        CancellationToken cancellationToken = default);
    Task<StandardResponse<LidarrAlbumRequestResultDTO>> RequestAlbumAsync(LidarrAlbumDTO selectedAlbum);
}
