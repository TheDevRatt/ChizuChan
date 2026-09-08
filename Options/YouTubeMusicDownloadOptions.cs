namespace ChizuChan.Options;

public sealed class YouTubeMusicDownloadOptions
{
    public const string SectionName = "YouTubeMusicDownload";

    public bool Enabled { get; set; }

    /// <summary>
    /// Must remain <see langword="true"/>. The configured YouTube library root is a dedicated
    /// security boundary: its directory and every descendant must be ACL-owned by the Chizu
    /// service account, no other process may have write access, and Plex requires read-only access.
    /// File locks coordinate cooperating Chizu instances but cannot protect against a writer that
    /// ignores them, so downloads fail closed when this invariant is disabled.
    /// </summary>
    public bool RequireExclusiveLibraryRoot { get; set; } = true;

    /// <summary>Maximum time to wait for the cross-process root operation lock.</summary>
    public int RootLockTimeoutSeconds { get; set; } = 30;

    public string LibraryRootPath { get; set; } = "";
    public string? YtDlpPath { get; set; }
    public string? FfmpegPath { get; set; }
    /// <summary>Optional media duration cap. Zero or negative disables the policy.</summary>
    public int MaxDurationSeconds { get; set; }
    /// <summary>Optional per-process elapsed cap. Zero or negative disables it; prefer stalled-work detection.</summary>
    public int DownloadTimeoutSeconds { get; set; }
    /// <summary>Optional new-file size cap. Zero or negative disables it. Existing library files are exempt.</summary>
    public long MaxFileSizeBytes { get; set; }
    /// <summary>Free-space floor on the actual library volume, not a per-file cap. Zero disables it.</summary>
    public long MinimumFreeSpaceBytes { get; set; } = 1024L * 1024 * 1024;
    /// <summary>Seconds with no output, staging file changes or CPU progress. Zero disables detection.</summary>
    public int StalledWorkTimeoutSeconds { get; set; } = 300;
    /// <summary>Storage/activity sampling period. Positive values are honored without clamping.</summary>
    public int ResourceMonitoringIntervalMilliseconds { get; set; } = 1000;
    public int MaxMetadataBytes { get; set; } = 256 * 1024;
    public string Genre { get; set; } = "YouTube";
    public string FallbackAlbum { get; set; } = "Single";
    public string FallbackArtist { get; set; } = "Unknown Artist";
    public string FallbackTitle { get; set; } = "Untitled";

    public int GetMaxDurationSeconds() => Math.Max(0, MaxDurationSeconds);
    public int GetDownloadTimeoutSeconds() => Math.Max(0, DownloadTimeoutSeconds);
    public int GetRootLockTimeoutSeconds() => Math.Clamp(RootLockTimeoutSeconds, 1, 5 * 60);
    public long GetMaxFileSizeBytes() => Math.Max(0, MaxFileSizeBytes);
    public int GetMaxMetadataBytes() => Math.Clamp(MaxMetadataBytes, 4 * 1024, 1024 * 1024);
}
