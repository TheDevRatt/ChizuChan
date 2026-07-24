using System.Globalization;
using System.Text;
using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Rest;

namespace ChizuChan.Services;

public sealed class MusicSearchEmbedBuilder : IMusicSearchEmbedBuilder
{
    private const int EmbedTitleLimit = 256;
    private const int EmbedDescriptionLimit = 4096;
    private const int EmbedFieldValueLimit = 1024;
    private const int ComponentCustomIdLimit = 100;
    private const string ActionCustomIdPrefix = "music_search_action:";
    private readonly bool _youtubeDownloadsEnabled;

    public MusicSearchEmbedBuilder() : this(youtubeDownloadsEnabled: true)
    {
    }

    public MusicSearchEmbedBuilder(IOptions<YouTubeMusicDownloadOptions> options)
        : this(options?.Value.Enabled ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    private MusicSearchEmbedBuilder(bool youtubeDownloadsEnabled) =>
        _youtubeDownloadsEnabled = youtubeDownloadsEnabled;

    public (EmbedProperties Embed, IMessageComponentProperties[] Components) Build(
        MusicSearchSessionSnapshot session) => Build(session.Query, session);

    public (EmbedProperties Embed, IMessageComponentProperties[] Components) Build(
        string query,
        MusicSearchSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(session.Pages);

        session = session with { Query = query.Trim() };

        var page = session.CurrentPage;
        if (page is null || (!session.LidarrAvailable && !session.YouTubeAvailable))
            return BuildEmpty(session);

        var embed = page.Kind == MusicSearchResultKind.YouTubeTrack
            ? BuildYouTubeEmbed(session, page)
            : BuildLidarrEmbed(session, page);

        var actionAvailable = page.Kind == MusicSearchResultKind.YouTubeTrack
            ? session.YouTubeAvailable
            : session.LidarrAvailable;
        return (embed, BuildComponents(
            page.Kind,
            session.TotalPages,
            actionAvailable,
            _youtubeDownloadsEnabled,
            session.ActionTokenSegment,
            session.CurrentIndex));
    }

    public EmbedProperties Build(string query, MusicSearchResultsDTO results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var description = BuildAlbumDescription(results);
        var fields = BuildYouTubeFields(results).ToArray();
        var thumbnail = results.YouTubeTracks
            .Select(track => track.ThumbnailUrl)
            .FirstOrDefault(IsAllowedThumbnailUrl);

        return new EmbedProperties
        {
            Title = $"Music search: {SanitizePlainText(query, 242)}",
            Description = description,
            Color = new Color(0x9B59B6),
            Thumbnail = thumbnail is null ? null : new EmbedThumbnailProperties(thumbnail),
            Fields = fields,
            Footer = new EmbedFooterProperties
            {
                Text = "Use /music_request result:<number> for albums. Use a YouTube URL with /play in a server.",
            },
        };
    }

    private static string BuildAlbumDescription(MusicSearchResultsDTO results)
    {
        var builder = new StringBuilder().AppendLine("### Albums you can request");

        if (!results.LidarrAvailable)
            return builder.Append("Lidarr search is temporarily unavailable.").ToString();

        if (results.Albums.Count == 0)
            return builder.Append("No requestable albums matched this query.").ToString();

        for (var index = 0; index < results.Albums.Count; index++)
        {
            var album = results.Albums[index];
            var artist = SanitizeMarkdown(album.Artist?.ArtistName ?? "Unknown artist", 55);
            var title = SanitizeMarkdown(album.Title ?? "Unknown album", 80);
            var metadata = new List<string>();
            if (!string.IsNullOrWhiteSpace(album.AlbumType))
                metadata.Add(SanitizeMarkdown(album.AlbumType, 30));
            if (album.ReleaseDate is not null)
                metadata.Add(album.ReleaseDate.Value.Year.ToString(CultureInfo.InvariantCulture));

            builder
                .Append("**").Append(index + 1).Append(". ").Append(artist)
                .Append("** • ").Append(title).AppendLine();

            if (metadata.Count > 0)
                builder.Append('`').Append(string.Join(" • ", metadata)).Append('`').AppendLine();

            if (!string.IsNullOrWhiteSpace(album.Overview))
                builder.Append("> ").Append(SanitizeMarkdown(album.Overview, 100)).AppendLine();

            builder.AppendLine();
        }

        var description = builder.ToString().TrimEnd();
        if (description.Length > EmbedDescriptionLimit)
            throw new InvalidOperationException("Rendered album results exceeded the Discord embed limit.");

        return description;
    }

    private static IEnumerable<EmbedFieldProperties> BuildYouTubeFields(MusicSearchResultsDTO results)
    {
        if (!results.YouTubeAvailable)
        {
            yield return new EmbedFieldProperties
            {
                Name = "YouTube track suggestions",
                Value = "YouTube search is temporarily unavailable.",
                Inline = false,
            };
            yield break;
        }

        if (results.YouTubeTracks.Count == 0)
        {
            yield return new EmbedFieldProperties
            {
                Name = "YouTube track suggestions",
                Value = "No matching YouTube tracks found.",
                Inline = false,
            };
            yield break;
        }

        var fieldIndex = 0;
        var value = NewYouTubeFieldValue(includeIntro: true);
        foreach (var track in results.YouTubeTracks)
        {
            var title = SanitizeMarkdown(track.Title, 90);
            var channel = SanitizeMarkdown(track.Channel ?? "Unknown channel", 50);
            var duration = track.Duration is null ? "duration unknown" : FormatDuration(track.Duration.Value);
            var titleAndLink = TryGetCanonicalVideoId(track, out var videoId)
                ? $"[{title}](https://www.youtube.com/watch?v={videoId})"
                : title;
            var entry = $"• {titleAndLink}\n  {channel} • `{duration}`\n";

            if (value.Length + entry.Length > EmbedFieldValueLimit)
            {
                yield return CreateYouTubeField(value, fieldIndex++);
                value = NewYouTubeFieldValue(includeIntro: false);
            }

            value.Append(entry);
        }

        if (value.Length > 0)
            yield return CreateYouTubeField(value, fieldIndex);
    }

    private static StringBuilder NewYouTubeFieldValue(bool includeIntro) =>
        new(includeIntro
            ? "*Playback links only. These are not numbered album requests.*\n"
            : string.Empty);

    private static EmbedFieldProperties CreateYouTubeField(StringBuilder value, int index) => new()
    {
        Name = index == 0 ? "YouTube track suggestions" : "YouTube track suggestions (continued)",
        Value = value.ToString().TrimEnd(),
        Inline = false,
    };

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
        int totalPages,
        bool actionAvailable,
        bool youtubeDownloadsEnabled,
        string actionTokenSegment,
        int index)
    {
        var disableNavigation = totalPages <= 1;
        var actionLabel = kind switch
        {
            MusicSearchResultKind.Album => "Request Album",
            MusicSearchResultKind.Single => "Request Single",
            MusicSearchResultKind.EP or MusicSearchResultKind.Release => "Request EP/Release",
            MusicSearchResultKind.YouTubeTrack when !youtubeDownloadsEnabled => "Downloads Disabled",
            MusicSearchResultKind.YouTubeTrack => "Download Single",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var row = new ActionRowProperties
        {
            new ButtonProperties("music_search_previous", "Previous", ButtonStyle.Primary)
            {
                Disabled = disableNavigation,
            },
            new ButtonProperties("music_search_next", "Next", ButtonStyle.Primary)
            {
                Disabled = disableNavigation,
            },
        };
        if (actionAvailable)
        {
            var actionCustomId = $"{ActionCustomIdPrefix}{actionTokenSegment}:{index}";
            if (actionCustomId.Length > ComponentCustomIdLimit)
                throw new InvalidOperationException("The music search action ID exceeded the Discord component limit.");

            row.Add(new ButtonProperties(actionCustomId, actionLabel, ButtonStyle.Success)
            {
                Disabled = kind == MusicSearchResultKind.YouTubeTrack && !youtubeDownloadsEnabled,
            });
        }

        return [row];
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
