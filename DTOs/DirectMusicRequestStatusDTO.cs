namespace ChizuChan.DTOs;

public sealed record SoulseekDownloadReceiptDTO(Guid BatchId)
{
    public Guid TransferId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Filename { get; init; } = string.Empty;
    public long Size { get; init; }
}

public enum DirectMusicRequestState
{
    DispatchPending,
    Queued,
    Downloading,
    DownloadedWaitingForPlex,
    ReadyInPlexamp,
    Failed,
}

/// <summary>Durable, non-secret status for one direct Soulseek track request.</summary>
public sealed record DirectMusicRequestStatusDTO
{
    public Guid RequestId { get; init; }
    public Guid BatchId { get; init; }
    public Guid SearchId { get; init; }
    public Guid? TransferId { get; init; }
    public ulong DiscordUserId { get; init; }
    public ulong DmChannelId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string RemoteFilename { get; init; } = string.Empty;
    public string ArtistName { get; init; } = string.Empty;
    public string TrackTitle { get; init; } = string.Empty;
    public long ExpectedSize { get; init; }
    public TimeSpan ExpectedDuration { get; init; }
    public DirectMusicRequestState State { get; init; }
    public long BytesTransferred { get; init; }
    public DateTimeOffset RequestedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public string? PlexMediaPath { get; init; }
    public string? FailureCategory { get; init; }
}
