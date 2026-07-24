namespace ChizuChan.DTOs;

/// <summary>
/// Durable completion information read from Lidarr. History identity is absent only when
/// Lidarr's album statistics fallback establishes completion.
/// </summary>
public sealed record LidarrAlbumCompletionDTO(
    bool IsComplete,
    int? HistoryRecordId,
    DateTimeOffset? CompletedAtUtc);
