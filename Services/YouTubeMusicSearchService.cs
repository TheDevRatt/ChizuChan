using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public sealed partial class YouTubeMusicSearchService : IYouTubeMusicSearchService
{
    private readonly IYtDlpSearchRunner _runner;
    private readonly YouTubeMusicSearchOptions _options;

    public YouTubeMusicSearchService(
        IYtDlpSearchRunner runner,
        IOptions<YouTubeMusicSearchOptions> options)
    {
        _runner = runner;
        _options = options.Value;
    }

    public async Task<StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>.ErrorResponse(
                "A search query is required.", (int)HttpStatusCode.BadRequest);
        }

        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length > _options.MaxQueryLength)
        {
            return StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>.ErrorResponse(
                "The search query is too long.", (int)HttpStatusCode.BadRequest);
        }

        var limit = Math.Clamp(_options.ResultLimit, 1, 10);
        var arguments = new[]
        {
            "--ignore-config",
            "--dump-single-json",
            "--flat-playlist",
            "--no-warnings",
            $"ytsearch{limit}:{trimmedQuery}",
        };

        try
        {
            var result = await _runner.RunAsync(
                arguments,
                TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 60)),
                cancellationToken);

            if (result.ExitCode != 0)
            {
                return StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>.ErrorResponse(
                    "YouTube search is unavailable right now.",
                    (int)HttpStatusCode.ServiceUnavailable);
            }

            return StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>.SuccessResponse(
                ParseResults(result.StandardOutput, limit));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>.ErrorResponse(
                "YouTube returned invalid search data.");
        }
        catch (Exception)
        {
            return StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>.ErrorResponse(
                "YouTube search is unavailable right now.",
                (int)HttpStatusCode.ServiceUnavailable);
        }
    }

    private static IReadOnlyList<YouTubeTrackSuggestionDTO> ParseResults(string json, int limit)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<YouTubeTrackSuggestionDTO>(limit);
        foreach (var entry in entries.EnumerateArray())
        {
            if (results.Count >= limit)
                break;
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var id = GetString(entry, "id");
            var title = GetString(entry, "title");
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(title) ||
                !YouTubeVideoIdPattern().IsMatch(id))
            {
                continue;
            }

            var duration = GetDuration(entry);
            results.Add(new YouTubeTrackSuggestionDTO
            {
                Title = title.Trim(),
                Channel = GetString(entry, "channel") ?? GetString(entry, "uploader"),
                Duration = duration,
                Url = $"https://www.youtube.com/watch?v={id}",
                ThumbnailUrl = GetThumbnail(entry),
            });
        }

        return results;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static TimeSpan? GetDuration(JsonElement entry)
    {
        if (!entry.TryGetProperty("duration", out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDouble(out var seconds) ||
            seconds <= 0 ||
            seconds > TimeSpan.FromHours(24).TotalSeconds)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static string? GetThumbnail(JsonElement entry)
    {
        var direct = GetString(entry, "thumbnail");
        if (IsAllowedThumbnailUrl(direct))
            return direct;

        if (!entry.TryGetProperty("thumbnails", out var thumbnails) ||
            thumbnails.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return thumbnails.EnumerateArray()
            .Where(thumbnail => thumbnail.ValueKind == JsonValueKind.Object)
            .Select(thumbnail => GetString(thumbnail, "url"))
            .LastOrDefault(IsAllowedThumbnailUrl);
    }

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

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex YouTubeVideoIdPattern();
}
