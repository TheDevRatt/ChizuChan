using ChizuChan.DTOs;

namespace ChizuChan.Services.Interfaces;

public interface IPlexMusicReadinessReader
{
    Task<StandardResponse<PlexTrackReadinessDTO>> FindTrackAsync(
        DirectMusicRequestStatusDTO request,
        CancellationToken cancellationToken = default);
}
