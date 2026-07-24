using ChizuChan.DTOs;

namespace ChizuChan.Services.Interfaces;

public interface IMusicSearchSessionService
{
    void SaveResults(
        ulong userId,
        ulong dmChannelId,
        string query,
        IEnumerable<MusicSearchResultPage> pages,
        bool lidarrAvailable = true,
        bool youtubeAvailable = true);

    bool BindMessage(ulong userId, ulong dmChannelId, ulong sourceMessageId);

    bool GetCurrent(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        out MusicSearchSessionSnapshot session);

    bool MovePrevious(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        out MusicSearchSessionSnapshot session);

    bool MoveNext(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        out MusicSearchSessionSnapshot session);

    // Compatibility for the existing slash-command flow until it is switched to component actions.
    void SaveResults(ulong userId, IEnumerable<LidarrAlbumDTO> results);
    bool TryGetResult(ulong userId, int oneBasedResultNumber, out LidarrAlbumDTO album);
    void ClearResults(ulong userId);
}
