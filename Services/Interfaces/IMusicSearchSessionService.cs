using ChizuChan.DTOs;

namespace ChizuChan.Services.Interfaces;

public interface IMusicSearchSessionService
{
    void SaveResults(ulong userId, IEnumerable<LidarrAlbumDTO> results);
    bool TryGetResult(ulong userId, int oneBasedResultNumber, out LidarrAlbumDTO album);
    void ClearResults(ulong userId);
}
