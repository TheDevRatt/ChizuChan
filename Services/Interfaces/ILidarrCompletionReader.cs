using ChizuChan.DTOs;

namespace ChizuChan.Services.Interfaces;

public interface ILidarrCompletionReader
{
    Task<StandardResponse<LidarrAlbumCompletionDTO>> GetCompletionAsync(
        int albumId,
        CancellationToken cancellationToken = default);
}
