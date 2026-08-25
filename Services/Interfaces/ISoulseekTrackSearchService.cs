using ChizuChan.DTOs;

namespace ChizuChan.Services.Interfaces;

public interface ISoulseekTrackSearchService
{
    Task<StandardResponse<IReadOnlyList<SoulseekTrackSuggestionDTO>>> SearchAsync(
        string query,
        CancellationToken cancellationToken);

    Task<StandardResponse<bool>> QueueDownloadAsync(
        SoulseekTrackSearchResult track,
        CancellationToken cancellationToken);
}
