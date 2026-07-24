namespace ChizuChan.Options;

public sealed class MusicRequestNotificationOptions
{
    public const string SectionName = "MusicRequestNotifications";

    public string StorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ChizuChan",
        "music-request-notifications.json");

    public int MaxRecords { get; set; } = 10_000;
    public int TerminalRetentionDays { get; set; } = 30;
    public int MaxFileSizeBytes { get; set; } = 8 * 1024 * 1024;
    public int PollIntervalSeconds { get; set; } = 60;
    public int InitialRetryDelaySeconds { get; set; } = 30;
    public int MaxRetryDelaySeconds { get; set; } = 3600;
}
