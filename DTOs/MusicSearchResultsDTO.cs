using System.Collections.Immutable;
using System.Text.Json;
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

public sealed record LidarrArtistSearchResult
{
    public LidarrArtistSearchResult(
        int? id,
        string? foreignArtistId,
        string? artistName,
        string? overview,
        ImmutableDictionary<string, JsonElement>? additionalData)
    {
        Id = id;
        ForeignArtistId = foreignArtistId;
        ArtistName = artistName;
        Overview = overview;
        AdditionalData = additionalData;
    }

    public int? Id { get; }
    public string? ForeignArtistId { get; }
    public string? ArtistName { get; }
    public string? Overview { get; }
    public ImmutableDictionary<string, JsonElement>? AdditionalData { get; }
}

public sealed record LidarrAlbumSearchResult
{
    public LidarrAlbumSearchResult(
        int? id,
        string? foreignAlbumId,
        string? title,
        DateTimeOffset? releaseDate,
        string? overview,
        string? albumType,
        LidarrArtistSearchResult? artist,
        ImmutableDictionary<string, JsonElement>? additionalData)
    {
        Id = id;
        ForeignAlbumId = foreignAlbumId;
        Title = title;
        ReleaseDate = releaseDate;
        Overview = overview;
        AlbumType = albumType;
        Artist = artist;
        AdditionalData = additionalData;
    }

    public int? Id { get; }
    public string? ForeignAlbumId { get; }
    public string? Title { get; }
    public DateTimeOffset? ReleaseDate { get; }
    public string? Overview { get; }
    public string? AlbumType { get; }
    public LidarrArtistSearchResult? Artist { get; }
    public ImmutableDictionary<string, JsonElement>? AdditionalData { get; }

    public LidarrAlbumDTO ToDto() => new()
    {
        Id = Id,
        ForeignAlbumId = ForeignAlbumId,
        Title = Title,
        ReleaseDate = ReleaseDate,
        Overview = Overview,
        AlbumType = AlbumType,
        Artist = Artist is null
            ? null
            : new LidarrArtistDTO
            {
                Id = Artist.Id,
                ForeignArtistId = Artist.ForeignArtistId,
                ArtistName = Artist.ArtistName,
                Overview = Artist.Overview,
                AdditionalData = CopyAdditionalData(Artist.AdditionalData),
            },
        AdditionalData = CopyAdditionalData(AdditionalData),
    };

    internal static LidarrAlbumSearchResult FromDto(LidarrAlbumDTO album) => new(
        album.Id,
        album.ForeignAlbumId,
        album.Title,
        album.ReleaseDate,
        album.Overview,
        album.AlbumType,
        album.Artist is null
            ? null
            : new LidarrArtistSearchResult(
                album.Artist.Id,
                album.Artist.ForeignArtistId,
                album.Artist.ArtistName,
                album.Artist.Overview,
                CopyAdditionalData(album.Artist.AdditionalData)?.ToImmutableDictionary()),
        CopyAdditionalData(album.AdditionalData)?.ToImmutableDictionary());

    private static Dictionary<string, JsonElement>? CopyAdditionalData(
        IEnumerable<KeyValuePair<string, JsonElement>>? additionalData) =>
        additionalData?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
}

public sealed record YouTubeTrackSearchResult
{
    public YouTubeTrackSearchResult(
        string videoId,
        string title,
        string? channel,
        TimeSpan? duration,
        string url,
        string? thumbnailUrl)
    {
        VideoId = videoId;
        Title = title;
        Channel = channel;
        Duration = duration;
        Url = url;
        ThumbnailUrl = thumbnailUrl;
    }

    public string VideoId { get; }
    public string Title { get; }
    public string? Channel { get; }
    public TimeSpan? Duration { get; }
    public string Url { get; }
    public string? ThumbnailUrl { get; }
}

/// <summary>
/// A server-created, typed and immutable page in a music search. Callers cannot supply an action URL or path.
/// </summary>
public sealed record MusicSearchResultPage
{
    private static readonly Regex YouTubeVideoIdPattern = new(
        "^[A-Za-z0-9_-]{11}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private MusicSearchResultPage(
        MusicSearchResultKind kind,
        LidarrAlbumSearchResult? lidarrAlbum,
        YouTubeTrackSearchResult? youTubeTrack)
    {
        Kind = kind;
        LidarrAlbum = lidarrAlbum;
        YouTubeTrack = youTubeTrack;
    }

    public MusicSearchResultKind Kind { get; }
    public LidarrAlbumSearchResult? LidarrAlbum { get; }
    public YouTubeTrackSearchResult? YouTubeTrack { get; }

    public static MusicSearchResultPage FromLidarr(LidarrAlbumDTO album)
    {
        ArgumentNullException.ThrowIfNull(album);

        var snapshot = LidarrAlbumSearchResult.FromDto(album);
        var kind = snapshot.AlbumType?.Trim() switch
        {
            string value when value.Equals("album", StringComparison.OrdinalIgnoreCase) => MusicSearchResultKind.Album,
            string value when value.Equals("single", StringComparison.OrdinalIgnoreCase) => MusicSearchResultKind.Single,
            string value when value.Equals("ep", StringComparison.OrdinalIgnoreCase) => MusicSearchResultKind.EP,
            _ => MusicSearchResultKind.Release,
        };

        return new MusicSearchResultPage(kind, snapshot, null);
    }

    public static MusicSearchResultPage FromYouTube(YouTubeTrackSuggestionDTO track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var videoId = track.VideoId?.Trim() ?? string.Empty;
        if (!YouTubeVideoIdPattern.IsMatch(videoId))
            throw new ArgumentException("A canonical 11-character YouTube video ID is required.", nameof(track));

        var canonicalTrack = new YouTubeTrackSearchResult(
            videoId,
            track.Title,
            track.Channel,
            track.Duration,
            $"https://www.youtube.com/watch?v={videoId}",
            track.ThumbnailUrl);

        return new MusicSearchResultPage(MusicSearchResultKind.YouTubeTrack, null, canonicalTrack);
    }
}

public sealed record MusicSearchSessionSnapshot
{
    public MusicSearchSessionSnapshot(
        string query,
        IEnumerable<MusicSearchResultPage> pages,
        int currentIndex,
        ulong ownerUserId,
        ulong dmChannelId,
        ulong sourceMessageId,
        bool lidarrAvailable,
        bool youtubeAvailable)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(pages);

        Query = query;
        Pages = pages.ToImmutableArray();
        CurrentIndex = currentIndex;
        OwnerUserId = ownerUserId;
        DmChannelId = dmChannelId;
        SourceMessageId = sourceMessageId;
        LidarrAvailable = lidarrAvailable;
        YouTubeAvailable = youtubeAvailable;
    }

    public string Query { get; init; }
    public IReadOnlyList<MusicSearchResultPage> Pages { get; }
    public int CurrentIndex { get; }
    public ulong OwnerUserId { get; }
    public ulong DmChannelId { get; }
    public ulong SourceMessageId { get; }
    public bool LidarrAvailable { get; init; }
    public bool YouTubeAvailable { get; init; }

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
