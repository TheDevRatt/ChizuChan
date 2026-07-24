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
    public int MaxDurationSeconds { get; set; } = 900;
    public int DownloadTimeoutSeconds { get; set; } = 300;
    public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024;
    public int MaxMetadataBytes { get; set; } = 256 * 1024;
    public string Genre { get; set; } = "YouTube";
    public string FallbackAlbum { get; set; } = "Single";
    public string FallbackArtist { get; set; } = "Unknown Artist";
    public string FallbackTitle { get; set; } = "Untitled";

    public int GetMaxDurationSeconds() => Math.Clamp(MaxDurationSeconds, 30, 6 * 60 * 60);
    public int GetDownloadTimeoutSeconds() => Math.Clamp(DownloadTimeoutSeconds, 10, 60 * 60);
    public int GetRootLockTimeoutSeconds() => Math.Clamp(RootLockTimeoutSeconds, 1, 5 * 60);
    public long GetMaxFileSizeBytes() => Math.Clamp(MaxFileSizeBytes, 1024 * 1024, 1024L * 1024 * 1024);
    public int GetMaxMetadataBytes() => Math.Clamp(MaxMetadataBytes, 4 * 1024, 1024 * 1024);
}
