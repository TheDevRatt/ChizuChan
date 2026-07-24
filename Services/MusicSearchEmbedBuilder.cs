using System.Globalization;
using System.Text;
using ChizuChan.DTOs;
using ChizuChan.Services.Interfaces;
using NetCord;
using NetCord.Rest;

namespace ChizuChan.Services;

public sealed class MusicSearchEmbedBuilder : IMusicSearchEmbedBuilder
{
    private const int EmbedTitleLimit = 256;
    private const int EmbedDescriptionLimit = 4096;

    public (EmbedProperties Embed, IMessageComponentProperties[] Components) Build(
        MusicSearchSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(session.Pages);

        var page = session.CurrentPage;
        if (page is null || (!session.LidarrAvailable && !session.YouTubeAvailable))
            return BuildEmpty(session);

        var embed = page.Kind == MusicSearchResultKind.YouTubeTrack
            ? BuildYouTubeEmbed(session, page)
            : BuildLidarrEmbed(session, page);

        return (embed, BuildComponents(page.Kind, session.TotalPages));
    }

    public EmbedProperties Build(string query, MusicSearchResultsDTO results)
    {
        ArgumentNullException.ThrowIfNull(results);

        MusicSearchResultPage? firstPage = results.Albums
            .Select(MusicSearchResultPage.FromLidarr)
            .FirstOrDefault();

        if (firstPage is null)
        {
            var track = results.YouTubeTracks.FirstOrDefault();
            if (track is not null && TryGetCanonicalVideoId(track, out var videoId))
            {
                track = new YouTubeTrackSuggestionDTO
                {
                    VideoId = videoId,
                    Title = track.Title,
                    Channel = track.Channel,
                    Duration = track.Duration,
                    Url = track.Url,
                    ThumbnailUrl = track.ThumbnailUrl,
                };
                firstPage = MusicSearchResultPage.FromYouTube(track);
            }
        }

        var pages = firstPage is null ? [] : new[] { firstPage };
        var snapshot = new MusicSearchSessionSnapshot(
            query,
            pages,
            0,
            0,
            0,
            0,
            results.LidarrAvailable,
            results.YouTubeAvailable);
        return Build(snapshot).Embed;
    }

    private static EmbedProperties BuildLidarrEmbed(
        MusicSearchSessionSnapshot session,
        MusicSearchResultPage page)
    {
        var album = page.LidarrAlbum ?? throw new InvalidOperationException("A Lidarr page requires album data.");
        var title = SanitizeMarkdown(album.Title ?? "Unknown release", 240);
        var artist = SanitizeMarkdown(album.Artist?.ArtistName ?? "Unknown artist", 160);
        var releaseType = page.Kind switch
        {
            MusicSearchResultKind.Album => "Album",
            MusicSearchResultKind.Single => "Single",
            MusicSearchResultKind.EP => "EP",
            _ => "Release",
        };

        var description = new StringBuilder()
            .Append("**Artist:** ").AppendLine(artist)
            .Append("**Type:** ").AppendLine(releaseType);

        if (album.ReleaseDate is not null)
        {
            description
                .Append("**Released:** ")
                .AppendLine(album.ReleaseDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(album.Overview))
        {
            description
                .AppendLine()
                .Append(SanitizeMarkdown(album.Overview, 3400));
        }

        AppendQuery(description, session.Query);

        return new EmbedProperties
        {
            Title = Truncate($"{releaseType}: {title}", EmbedTitleLimit),
            Description = Truncate(description.ToString().Trim(), EmbedDescriptionLimit),
            Color = new Color(0x9B59B6),
            Footer = BuildFooter(session),
        };
    }

    private static EmbedProperties BuildYouTubeEmbed(
        MusicSearchSessionSnapshot session,
        MusicSearchResultPage page)
    {
        var track = page.YouTubeTrack ?? throw new InvalidOperationException("A YouTube page requires track data.");
        var title = SanitizeMarkdown(track.Title, 240);
        var channel = SanitizeMarkdown(track.Channel ?? "Unknown channel", 160);
        var duration = track.Duration is null ? "Unknown" : FormatDuration(track.Duration.Value);
        var canonicalUrl = $"https://www.youtube.com/watch?v={track.VideoId}";
        var description = new StringBuilder()
            .Append("**Channel:** ").AppendLine(channel)
            .Append("**Duration:** ").AppendLine(duration)
            .Append("**YouTube:** [Open track](").Append(canonicalUrl).AppendLine(")");

        AppendQuery(description, session.Query);

        var thumbnail = IsAllowedThumbnailUrl(track.ThumbnailUrl) ? track.ThumbnailUrl : null;
        return new EmbedProperties
        {
            Title = Truncate($"YouTube track: {title}", EmbedTitleLimit),
            Description = Truncate(description.ToString().Trim(), EmbedDescriptionLimit),
            Color = new Color(0xFF0000),
            Thumbnail = thumbnail is null ? null : new EmbedThumbnailProperties(thumbnail),
            Footer = BuildFooter(session),
        };
    }

    private static (EmbedProperties Embed, IMessageComponentProperties[] Components) BuildEmpty(
        MusicSearchSessionSnapshot session)
    {
        var lidarrStatus = session.LidarrAvailable
            ? "No matching Lidarr releases found."
            : "Lidarr search is temporarily unavailable.";
        var youtubeStatus = session.YouTubeAvailable
            ? "No matching YouTube tracks found."
            : "YouTube search is temporarily unavailable.";
        var description = $"**Lidarr:** {lidarrStatus}\n**YouTube:** {youtubeStatus}";

        var embed = new EmbedProperties
        {
            Title = $"Music search: {SanitizePlainText(session.Query, 242)}",
            Description = description,
            Color = new Color(0x9B59B6),
            Footer = new EmbedFooterProperties { Text = "No results" },
        };
        return (embed, []);
    }

    private static IMessageComponentProperties[] BuildComponents(
        MusicSearchResultKind kind,
        int totalPages)
    {
        var disableNavigation = totalPages <= 1;
        var actionLabel = kind switch
        {
            MusicSearchResultKind.Album => "Request Album",
            MusicSearchResultKind.Single => "Request Single",
            MusicSearchResultKind.EP or MusicSearchResultKind.Release => "Request EP/Release",
            MusicSearchResultKind.YouTubeTrack => "Download Single",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        return
        [
            new ActionRowProperties
            {
                new ButtonProperties("music_search_previous", "Previous", ButtonStyle.Primary)
                {
                    Disabled = disableNavigation,
                },
                new ButtonProperties("music_search_next", "Next", ButtonStyle.Primary)
                {
                    Disabled = disableNavigation,
                },
                new ButtonProperties("music_search_action", actionLabel, ButtonStyle.Success),
            },
        ];
    }

    private static EmbedFooterProperties BuildFooter(MusicSearchSessionSnapshot session) => new()
    {
        Text = $"Result {session.CurrentIndex + 1} of {session.TotalPages}",
    };

    private static void AppendQuery(StringBuilder description, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        description
            .AppendLine()
            .AppendLine()
            .Append("**Search:** ")
            .Append(SanitizeMarkdown(query, 300));
    }

    private static bool TryGetCanonicalVideoId(YouTubeTrackSuggestionDTO track, out string videoId)
    {
        videoId = track.VideoId?.Trim() ?? string.Empty;
        if (IsCanonicalVideoId(videoId))
            return true;

        if (Uri.TryCreate(track.Url, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)))
        {
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in query)
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == "v" && IsCanonicalVideoId(parts[1]))
                {
                    videoId = parts[1];
                    return true;
                }
            }
        }

        videoId = string.Empty;
        return false;
    }

    private static bool IsCanonicalVideoId(string value) =>
        value.Length == 11 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";

    private static bool IsAllowedThumbnailUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return uri.Host.Equals("ytimg.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".ytimg.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeMarkdown(string value, int maximumLength) =>
        Truncate(EscapeMarkdown(NormalizeExternalText(value)), maximumLength);

    private static string SanitizePlainText(string value, int maximumLength) =>
        Truncate(NormalizeExternalText(value), maximumLength);

    private static string NormalizeExternalText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                    builder.Append(' ');
                previousWasSpace = true;
                continue;
            }

            previousWasSpace = false;
            builder.Append(character switch
            {
                '<' => '‹',
                '>' => '›',
                '@' => '＠',
                '`' => '′',
                _ => character,
            });
        }

        return builder.ToString().Trim();
    }

    private static string EscapeMarkdown(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("~", "\\~", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("#", "\\#", StringComparison.Ordinal);

    private static string Truncate(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
            return value;

        var builder = new StringBuilder(maximumLength);
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (builder.Length + element.Length + 1 > maximumLength)
                break;
            builder.Append(element);
        }

        return builder.Append('…').ToString();
    }
}
