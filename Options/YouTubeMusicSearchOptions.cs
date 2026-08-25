namespace ChizuChan.Options;

public sealed class YouTubeMusicSearchOptions
{
    public const string SectionName = "YouTubeMusicSearch";

    public int ResultLimit { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 20;
    public int MaxQueryLength { get; set; } = 100;
    public int BootstrapCacheMinutes { get; set; } = 60;
    public int MaxResponseBytes { get; set; } = 2_000_000;
}
