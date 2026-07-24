using ChizuChan.DTOs;

namespace ChizuChan.Services.Interfaces;

public interface IMusicRequestNotificationStore : IDisposable, IAsyncDisposable
{
    Task<MusicRequestNotificationDTO> AddAsync(
        MusicRequestNotificationDTO notification,
        CancellationToken cancellationToken = default);

    Task<MusicRequestNotificationDTO> AddOrGetAsync(
        MusicRequestNotificationDTO notification,
        CancellationToken cancellationToken = default);

    Task<MusicRequestNotificationDTO?> GetAsync(
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MusicRequestNotificationDTO>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MusicRequestNotificationDTO>> GetActiveAsync(
        CancellationToken cancellationToken = default);

    Task<MusicRequestNotificationDTO> MarkCompletionObservedAsync(
        Guid requestId,
        int? completionHistoryId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);

    Task<MusicRequestNotificationDTO> RecordAttemptFailureAsync(
        Guid requestId,
        string errorCategory,
        DateTimeOffset? nextAttemptAtUtc,
        CancellationToken cancellationToken = default);

    Task<MusicRequestNotificationDTO> MarkNotifiedAsync(
        Guid requestId,
        ulong notificationMessageId,
        CancellationToken cancellationToken = default);

    Task<MusicRequestNotificationDTO> MarkDeadLetterAsync(
        Guid requestId,
        string errorCategory,
        CancellationToken cancellationToken = default);
}
