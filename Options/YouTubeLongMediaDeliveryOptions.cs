namespace ChizuChan.Options;

/// <summary>Concurrency and delivered-history retention, never media storage or execution limits.</summary>
public sealed class YouTubeLongMediaDeliveryOptions
{
    public const string SectionName = "YouTubeLongMediaDelivery";
    public string StorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData),
        "ChizuChan", "youtube-long-media-jobs.json");
    public int MaxPendingJobs { get; set; } = 16;
    /// <summary>Recent delivered terminal records to retain. Pending jobs and undelivered results are never pruned.</summary>
    public int MaxStoredJobs { get; set; } = 1000;
    public int Concurrency { get; set; } = 1;
    public int PollIntervalMilliseconds { get; set; } = 1000;
    public int DeliveryRetrySeconds { get; set; } = 30;
}
