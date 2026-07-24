namespace ChizuChan.Options;

public class LidarrOptions
{
    public const string SectionName = "Lidarr";

    public string BaseUrl { get; set; } = "http://localhost:8686";
    public string ApiKey { get; set; } = string.Empty;
    public string RootFolderPath { get; set; } = "/data/media/music";
    public int QualityProfileId { get; set; } = 4;
    public int MetadataProfileId { get; set; } = 1;
    public int MaxQueryLength { get; set; } = 100;
    public int SessionLifetimeMinutes { get; set; } = 15;
    public int MaxSessions { get; set; } = 100;
    public ulong[] AllowedUserIds { get; set; } = [814996982967566367UL];
    public int SearchCooldownSeconds { get; set; } = 5;
    public int SearchTimeoutSeconds { get; set; } = 30;
    public int RequestCooldownSeconds { get; set; } = 30;
    public int GlobalOperationsPerMinute { get; set; } = 20;
}
