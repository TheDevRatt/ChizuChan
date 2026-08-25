namespace ChizuChan.Options;

public sealed class DirectMusicRequestStatusOptions
{
    public const string SectionName = "DirectMusicRequestStatus";

    public string StorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ChizuChan",
        "direct-music-request-status.json");

    public int MaxRecords { get; set; } = 10_000;
    public int TerminalRetentionDays { get; set; } = 30;
    public int MaxFileSizeBytes { get; set; } = 8 * 1024 * 1024;
    public int PollIntervalSeconds { get; set; } = 15;
    public int MaxRequestsPerPoll { get; set; } = 20;
    public int PlexReadyTimeoutHours { get; set; } = 24;
    public int DispatchGraceSeconds { get; set; } = 120;
    public string PlexBaseUrl { get; set; } = string.Empty;
    public string PlexToken { get; set; } = string.Empty;
    public int PlexMusicSectionId { get; set; } = 3;
    public int HttpTimeoutSeconds { get; set; } = 10;
    public int MaxResponseBytes { get; set; } = 512 * 1024;
}
