namespace ChizuChan.DTOs;

public sealed class YouTubeTrackSuggestionDTO
{
    public string Title { get; set; } = string.Empty;
    public string? Channel { get; set; }
    public TimeSpan? Duration { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
}

public sealed class MusicSearchResultsDTO
{
    public IReadOnlyList<LidarrAlbumDTO> Albums { get; set; } = [];
    public IReadOnlyList<YouTubeTrackSuggestionDTO> YouTubeTracks { get; set; } = [];
    public bool LidarrAvailable { get; set; }
    public bool YouTubeAvailable { get; set; }
}
