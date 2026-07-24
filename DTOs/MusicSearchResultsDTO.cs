using System.Text.RegularExpressions;

namespace ChizuChan.DTOs;

public sealed class YouTubeTrackSuggestionDTO
{
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Channel { get; set; }
    public TimeSpan? Duration { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
}

public enum MusicSearchResultKind
{
    Album,
    Single,
    EP,
    Release,
    YouTubeTrack,
}

/// <summary>
/// A server-created, typed page in a music search. Callers cannot supply an action URL or path.
/// </summary>
public sealed record MusicSearchResultPage
{
    private static readonly Regex YouTubeVideoIdPattern = new(
        "^[A-Za-z0-9_-]{11}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private MusicSearchResultPage(
        MusicSearchResultKind kind,
        LidarrAlbumDTO? lidarrAlbum,
        YouTubeTrackSuggestionDTO? youTubeTrack)
    {
        Kind = kind;
        LidarrAlbum = lidarrAlbum;
        YouTubeTrack = youTubeTrack;
    }

    public MusicSearchResultKind Kind { get; }
    public LidarrAlbumDTO? LidarrAlbum { get; }
    public YouTubeTrackSuggestionDTO? YouTubeTrack { get; }

    public static MusicSearchResultPage FromLidarr(LidarrAlbumDTO album)
    {
        ArgumentNullException.ThrowIfNull(album);

        var kind = album.AlbumType?.Trim() switch
        {
            string value when value.Equals("album", StringComparison.OrdinalIgnoreCase) => MusicSearchResultKind.Album,
            string value when value.Equals("single", StringComparison.OrdinalIgnoreCase) => MusicSearchResultKind.Single,
            string value when value.Equals("ep", StringComparison.OrdinalIgnoreCase) => MusicSearchResultKind.EP,
            _ => MusicSearchResultKind.Release,
        };

        return new MusicSearchResultPage(kind, album, null);
    }

    public static MusicSearchResultPage FromYouTube(YouTubeTrackSuggestionDTO track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var videoId = track.VideoId?.Trim() ?? string.Empty;
        if (!YouTubeVideoIdPattern.IsMatch(videoId))
            throw new ArgumentException("A canonical 11-character YouTube video ID is required.", nameof(track));

        // Copy the suggestion and derive the URL from the validated ID. A provider/caller URL is never trusted.
        var canonicalTrack = new YouTubeTrackSuggestionDTO
        {
            VideoId = videoId,
            Title = track.Title,
            Channel = track.Channel,
            Duration = track.Duration,
            Url = $"https://www.youtube.com/watch?v={videoId}",
            ThumbnailUrl = track.ThumbnailUrl,
        };

        return new MusicSearchResultPage(MusicSearchResultKind.YouTubeTrack, null, canonicalTrack);
    }
}

public sealed record MusicSearchSessionSnapshot(
    string Query,
    IReadOnlyList<MusicSearchResultPage> Pages,
    int CurrentIndex,
    ulong OwnerUserId,
    ulong DmChannelId,
    ulong SourceMessageId,
    bool LidarrAvailable,
    bool YouTubeAvailable)
{
    public MusicSearchResultPage? CurrentPage =>
        CurrentIndex >= 0 && CurrentIndex < Pages.Count ? Pages[CurrentIndex] : null;

    public int TotalPages => Pages.Count;
}

public sealed class MusicSearchResultsDTO
{
    public IReadOnlyList<LidarrAlbumDTO> Albums { get; set; } = [];
    public IReadOnlyList<YouTubeTrackSuggestionDTO> YouTubeTracks { get; set; } = [];
    public bool LidarrAvailable { get; set; }
    public bool YouTubeAvailable { get; set; }
}
