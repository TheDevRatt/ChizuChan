using ChizuChan.DTOs;

namespace ChizuChan.Services.Interfaces;

public interface IYouTubeMusicSearchService
{
    Task<StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>> SearchAsync(
        string query,
        CancellationToken cancellationToken);
}
