namespace ChizuChan.DTOs;

public enum SoulseekDownloadBatchState
{
    Queued,
    Downloading,
    Succeeded,
    Failed,
}

public sealed record SoulseekDownloadBatchStatusDTO(
    SoulseekDownloadBatchState State,
    Guid? TransferId,
    long BytesTransferred,
    long Size,
    string? FailureCategory);

public sealed record PlexTrackReadinessDTO(bool IsReady, string? MediaIdentity);
