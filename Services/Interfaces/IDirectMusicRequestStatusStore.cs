using ChizuChan.DTOs;

namespace ChizuChan.Services.Interfaces;

public interface IDirectMusicRequestStatusStore : IDisposable, IAsyncDisposable
{
    Task<DirectMusicRequestStatusDTO> AddAsync(
        DirectMusicRequestStatusDTO request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DirectMusicRequestStatusDTO>> GetActiveAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DirectMusicRequestStatusDTO>> GetRecentForUserAsync(
        ulong discordUserId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<DirectMusicRequestStatusDTO> UpdateAsync(
        DirectMusicRequestStatusDTO request,
        CancellationToken cancellationToken = default);
}
