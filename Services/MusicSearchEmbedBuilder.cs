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
    private const int EmbedFieldValueLimit = 1024;

    public EmbedProperties Build(string query, MusicSearchResultsDTO results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var description = BuildAlbumDescription(results);
        var fields = BuildYouTubeFields(results).ToList();
        var thumbnail = results.YouTubeTracks
            .Select(track => track.ThumbnailUrl)
            .FirstOrDefault(IsAllowedThumbnailUrl);

        return new EmbedProperties
        {
            Title = $"Music search: {SanitizePlainText(query, 242)}",
            Description = description,
            Color = new Color(0x9B59B6),
            Thumbnail = thumbnail is null ? null : new EmbedThumbnailProperties(thumbnail),
            Fields = fields.ToArray(),
            Footer = new EmbedFooterProperties
            {
                Text = "Use /music_request result:<number> for albums. Use a YouTube URL with /play in a server.",
            },
        };
    }

    private static string BuildAlbumDescription(MusicSearchResultsDTO results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("### Albums you can request");

        if (!results.LidarrAvailable)
        {
            builder.Append("Lidarr search is temporarily unavailable.");
            return builder.ToString();
        }

        if (results.Albums.Count == 0)
        {
            builder.Append("No requestable albums matched this query.");
            return builder.ToString();
        }

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
                .Append("**")
                .Append(index + 1)
                .Append(". ")
                .Append(artist)
                .Append("** • ")
                .Append(title)
                .AppendLine();

            if (metadata.Count > 0)
                builder.Append('`').Append(string.Join(" • ", metadata)).Append('`').AppendLine();

            if (!string.IsNullOrWhiteSpace(album.Overview))
                builder.Append('>').Append(' ').Append(SanitizeMarkdown(album.Overview, 100)).AppendLine();

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
            var entry = $"• [{title}]({track.Url})\n  {channel} • `{duration}`\n";

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
            .Replace("`", "\\`", StringComparison.Ordinal)
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
