namespace ChizuChan.Options;

public sealed class SoulseekTrackSearchOptions
{
    public const string SectionName = "SoulseekTrackSearch";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:5030";
    public string ApiKey { get; set; } = string.Empty;
    public int ResultLimit { get; set; } = 5;
    public int SearchTimeoutSeconds { get; set; } = 8;
    public int HttpTimeoutSeconds { get; set; } = 15;
    public bool SearchTimeoutUsesMilliseconds { get; set; }
    public int ResponseLimit { get; set; } = 20;
    public int FileLimit { get; set; } = 100;
    public int MaxQueryLength { get; set; } = 100;

    public int GetEffectiveSearchTimeoutSeconds() => Math.Clamp(SearchTimeoutSeconds, 5, 30);

    public int GetSlskdSearchTimeout()
    {
        var seconds = GetEffectiveSearchTimeoutSeconds();
        return SearchTimeoutUsesMilliseconds ? seconds * 1000 : seconds;
    }

    public int GetEffectiveHttpTimeoutSeconds() =>
        Math.Clamp(HttpTimeoutSeconds, GetEffectiveSearchTimeoutSeconds() + 2, 55);

    public int GetMinimumCommandTimeoutSeconds() =>
        Math.Clamp(GetEffectiveHttpTimeoutSeconds() + 2, 5, 60);
}
