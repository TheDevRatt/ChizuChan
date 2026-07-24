using ChizuChan.DTOs;

namespace ChizuChan.Services.Interfaces;

public interface IMusicSearchSessionService
{
    MusicSearchSessionToken SaveResults(
        ulong userId,
        ulong dmChannelId,
        string query,
        IEnumerable<MusicSearchResultPage> pages,
        bool lidarrAvailable = true,
        bool youtubeAvailable = true);

    bool BindMessage(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        MusicSearchSessionToken token);

    bool GetUnboundCurrent(
        ulong userId,
        ulong dmChannelId,
        MusicSearchSessionToken token,
        out MusicSearchSessionSnapshot session);

    bool GetCurrent(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        out MusicSearchSessionSnapshot session);

    bool TryClaimRenderedSelection(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        string actionTokenSegment,
        int index,
        out MusicSearchSelectionClaim claim);

    void ReleaseSelectionClaim(MusicSearchSelectionClaim claim);

    bool PreparePrevious(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        out MusicSearchSessionSnapshot session,
        out MusicSearchRenderCommit renderCommit);

    bool PrepareNext(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        out MusicSearchSessionSnapshot session,
        out MusicSearchRenderCommit renderCommit);

    bool CommitRendered(MusicSearchRenderCommit renderCommit);

    // Compatibility for the existing slash-command flow until it is switched to component actions.
    void SaveResults(ulong userId, IEnumerable<LidarrAlbumDTO> results);
    bool TryGetResult(ulong userId, int oneBasedResultNumber, out LidarrAlbumDTO album);
    void ClearResults(ulong userId);
}
