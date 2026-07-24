namespace ChizuChan.DTOs;

public enum MusicRequestNotificationState
{
    Pending,
    CompletionObserved,
    Notified,
    DeadLetter,
}

/// <summary>
/// Durable, non-secret information needed to notify one Discord user when a Lidarr album completes.
/// </summary>
public sealed record MusicRequestNotificationDTO
{
    public Guid RequestId { get; init; }
    public int LidarrAlbumId { get; init; }
    public string ForeignAlbumId { get; init; } = string.Empty;
    public ulong DiscordUserId { get; init; }
    public ulong DmChannelId { get; init; }
    public string ArtistName { get; init; } = string.Empty;
    public string AlbumTitle { get; init; } = string.Empty;
    public DateTimeOffset RequestedAtUtc { get; init; }
    public MusicRequestNotificationState State { get; init; }
    public int? CompletionHistoryId { get; init; }
    public DateTimeOffset? CompletionObservedAtUtc { get; init; }
    public string NotificationNonce { get; init; } = string.Empty;
    public ulong? NotificationMessageId { get; init; }
    public int AttemptCount { get; init; }
    public DateTimeOffset? NextAttemptAtUtc { get; init; }
    public string? LastErrorCategory { get; init; }
}
