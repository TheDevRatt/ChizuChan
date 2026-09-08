namespace ChizuChan.Options;

/// <summary>Queue/storage policy only. These settings never limit media duration or execution time.</summary>
public sealed class YouTubeLongMediaDeliveryOptions
{
    public const string SectionName = "YouTubeLongMediaDelivery";
    public string StorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData),
        "ChizuChan", "youtube-long-media-jobs.json");
    public int MaxPendingJobs { get; set; } = 16;
    public int MaxStoredJobs { get; set; } = 1000;
    public int MaxStoreBytes { get; set; } = 16 * 1024 * 1024;
    public int Concurrency { get; set; } = 1;
    public int PollIntervalMilliseconds { get; set; } = 1000;
    public int DeliveryRetrySeconds { get; set; } = 30;
}
