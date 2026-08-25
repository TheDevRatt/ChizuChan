using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public sealed partial class YouTubeMusicSearchService : IYouTubeMusicSearchService
{
    private const string BootstrapUrl = "https://music.youtube.com/";
    private const string SongsFilter = "EgWKAQIIAWoKEAkQBRAKEAMQBA==";
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly YouTubeMusicSearchOptions _options;
    private readonly SemaphoreSlim _bootstrapLock = new(1, 1);
    private BootstrapConfiguration? _bootstrap;
    private int ResponseLimitBytes => Math.Clamp(_options.MaxResponseBytes, 64 * 1024, MaximumResponseBytes);

    public YouTubeMusicSearchService(
        IHttpClientFactory httpClientFactory,
        IOptions<YouTubeMusicSearchOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Error("A search query is required.", HttpStatusCode.BadRequest);

        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length > Math.Clamp(_options.MaxQueryLength, 1, 200))
            return Error("The search query is too long.", HttpStatusCode.BadRequest);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 60)));
            var bootstrap = await GetBootstrapAsync(timeout.Token);
            if (bootstrap is null)
                return Error("YouTube Music search is unavailable right now.");

            var endpoint = new Uri(
                $"https://music.youtube.com/youtubei/v1/search?prettyPrint=false&key={Uri.EscapeDataString(bootstrap.ApiKey)}");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            request.Headers.Referrer = new Uri(BootstrapUrl);
            request.Content = JsonContent.Create(new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "WEB_REMIX",
                        clientVersion = bootstrap.ClientVersion,
                    },
                },
                query = trimmedQuery,
                @params = SongsFilter,
            });

            using var response = await _httpClientFactory.CreateClient(nameof(YouTubeMusicSearchService))
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return Error("YouTube Music search is unavailable right now.", response.StatusCode);

            var json = await ReadBoundedStringAsync(response.Content, ResponseLimitBytes, timeout.Token);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var results = ParseResults(document.RootElement, Math.Clamp(_options.ResultLimit, 1, 10));
            return StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>.SuccessResponse(results);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Error("YouTube Music search is unavailable right now.");
        }
    }

    private async Task<BootstrapConfiguration?> GetBootstrapAsync(CancellationToken cancellationToken)
    {
        var cached = _bootstrap;
        if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached;

        await _bootstrapLock.WaitAsync(cancellationToken);
        try
        {
            cached = _bootstrap;
            if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow)
                return cached;

            using var request = new HttpRequestMessage(HttpMethod.Get, BootstrapUrl);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            using var response = await _httpClientFactory.CreateClient(nameof(YouTubeMusicSearchService))
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var html = await ReadBoundedStringAsync(response.Content, ResponseLimitBytes, cancellationToken);
            var key = InnertubeApiKeyPattern().Match(html).Groups["value"].Value;
            var version = InnertubeClientVersionPattern().Match(html).Groups["value"].Value;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(version) ||
                key.Length > 256 || version.Length > 100)
            {
                return null;
            }

            cached = new BootstrapConfiguration(
                key,
                version,
                DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.BootstrapCacheMinutes, 1, 1440)));
            _bootstrap = cached;
            return cached;
        }
        finally
        {
            _bootstrapLock.Release();
        }
    }

    private static IReadOnlyList<YouTubeTrackSuggestionDTO> ParseResults(JsonElement root, int limit)
    {
        var results = new List<YouTubeTrackSuggestionDTO>(limit);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in FindRenderers(root, "musicResponsiveListItemRenderer"))
        {
            if (results.Count >= limit)
                break;
            if (!TryGetAtvTrack(item, out var videoId, out var title) || !seen.Add(videoId))
                continue;

            results.Add(new YouTubeTrackSuggestionDTO
            {
                VideoId = videoId,
                Title = Bound(title, 240),
                Channel = GetArtist(item),
                Duration = GetDuration(item),
                Url = $"https://www.youtube.com/watch?v={videoId}",
                ThumbnailUrl = GetThumbnail(item),
            });
        }

        return results;
    }

    private static IEnumerable<JsonElement> FindRenderers(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals(propertyName) && property.Value.ValueKind == JsonValueKind.Object)
                    yield return property.Value;

                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    foreach (var nested in FindRenderers(property.Value, propertyName))
                        yield return nested;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in root.EnumerateArray())
            {
                if (child.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    continue;
                foreach (var nested in FindRenderers(child, propertyName))
                    yield return nested;
            }
        }
    }

    private static bool TryGetAtvTrack(JsonElement item, out string videoId, out string title)
    {
        videoId = string.Empty;
        title = string.Empty;
        if (!item.TryGetProperty("flexColumns", out var columns) || columns.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var column in columns.EnumerateArray())
        {
            if (!TryGetRuns(column, out var runs))
                continue;
            foreach (var run in runs.EnumerateArray())
            {
                if (run.ValueKind != JsonValueKind.Object ||
                    !run.TryGetProperty("text", out var textValue) || textValue.ValueKind != JsonValueKind.String ||
                    !run.TryGetProperty("navigationEndpoint", out var navigation) ||
                    !navigation.TryGetProperty("watchEndpoint", out var watch) ||
                    !watch.TryGetProperty("videoId", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
                    !watch.TryGetProperty("watchEndpointMusicSupportedConfigs", out var supported) ||
                    !supported.TryGetProperty("watchEndpointMusicConfig", out var config) ||
                    !config.TryGetProperty("musicVideoType", out var type) || type.ValueKind != JsonValueKind.String ||
                    !string.Equals(type.GetString(), "MUSIC_VIDEO_TYPE_ATV", StringComparison.Ordinal))
                {
                    continue;
                }

                var candidateId = idValue.GetString();
                var candidateTitle = textValue.GetString()?.Trim();
                if (candidateId is not null && candidateTitle is not null && YouTubeVideoIdPattern().IsMatch(candidateId))
                {
                    videoId = candidateId;
                    title = candidateTitle;
                    return title.Length > 0;
                }
            }
        }

        return false;
    }

    private static string? GetArtist(JsonElement item)
    {
        if (!item.TryGetProperty("flexColumns", out var columns) || columns.ValueKind != JsonValueKind.Array)
            return null;
        var index = 0;
        foreach (var column in columns.EnumerateArray())
        {
            if (index++ == 0 || !TryGetRuns(column, out var runs))
                continue;
            foreach (var run in runs.EnumerateArray())
            {
                if (!run.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    continue;
                var value = text.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value) && value != "•" &&
                    !value.Equals("Song", StringComparison.OrdinalIgnoreCase))
                {
                    return Bound(value, 160);
                }
            }
        }
        return null;
    }

    private static TimeSpan? GetDuration(JsonElement item)
    {
        if (item.TryGetProperty("fixedColumns", out var fixedColumns) &&
            fixedColumns.ValueKind == JsonValueKind.Array)
        {
            foreach (var column in fixedColumns.EnumerateArray())
            {
                if (!column.TryGetProperty("musicResponsiveListItemFixedColumnRenderer", out var renderer) ||
                    !renderer.TryGetProperty("text", out var text) ||
                    !text.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                if (FindDuration(runs) is { } duration)
                    return duration;
            }
        }

        if (item.TryGetProperty("flexColumns", out var flexColumns) &&
            flexColumns.ValueKind == JsonValueKind.Array)
        {
            foreach (var column in flexColumns.EnumerateArray())
            {
                if (TryGetRuns(column, out var runs) && FindDuration(runs) is { } duration)
                    return duration;
            }
        }

        return null;
    }

    private static TimeSpan? FindDuration(JsonElement runs)
    {
        foreach (var run in runs.EnumerateArray())
        {
            if (!run.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
                continue;
            if (TryParseDuration(value.GetString(), out var duration))
                return duration;
        }
        return null;
    }

    private static bool TryParseDuration(string? value, out TimeSpan duration)
    {
        duration = default;
        var parts = value?.Split(':');
        if (parts is null || parts.Length is < 2 or > 3 || parts.Any(part => !int.TryParse(part, out _)))
            return false;
        var numbers = parts.Select(int.Parse).ToArray();
        if (numbers.Any(number => number < 0) || numbers[^1] > 59 || numbers[^2] > 59)
            return false;
        duration = parts.Length == 2
            ? new TimeSpan(0, numbers[0], numbers[1])
            : new TimeSpan(numbers[0], numbers[1], numbers[2]);
        return duration > TimeSpan.Zero && duration <= TimeSpan.FromHours(24);
    }

    private static string? GetThumbnail(JsonElement item)
    {
        if (!item.TryGetProperty("thumbnail", out var thumbnail) ||
            !thumbnail.TryGetProperty("musicThumbnailRenderer", out var renderer) ||
            !renderer.TryGetProperty("thumbnail", out var inner) ||
            !inner.TryGetProperty("thumbnails", out var thumbnails) || thumbnails.ValueKind != JsonValueKind.Array)
            return null;
        return thumbnails.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.Object && element.TryGetProperty("url", out _))
            .Select(element => element.GetProperty("url"))
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString())
            .LastOrDefault(IsAllowedThumbnailUrl);
    }

    private static bool TryGetRuns(JsonElement column, out JsonElement runs)
    {
        runs = default;
        return column.ValueKind == JsonValueKind.Object &&
               column.TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var renderer) &&
               renderer.TryGetProperty("text", out var text) &&
               text.TryGetProperty("runs", out runs) && runs.ValueKind == JsonValueKind.Array;
    }

    private static bool IsAllowedThumbnailUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            return false;
        return uri.Host.Equals("ytimg.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".ytimg.com", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadBoundedStringAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
            throw new InvalidDataException("Provider response exceeded its limit.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Provider response exceeded its limit.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>> Error(
        string message,
        HttpStatusCode status = HttpStatusCode.ServiceUnavailable) =>
        StandardResponse<IReadOnlyList<YouTubeTrackSuggestionDTO>>.ErrorResponse(message, (int)status);

    private sealed record BootstrapConfiguration(string ApiKey, string ClientVersion, DateTimeOffset ExpiresAt);

    [GeneratedRegex("\\\"INNERTUBE_API_KEY\\\"\\s*:\\s*\\\"(?<value>[^\\\"\\\\]{1,256})\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex InnertubeApiKeyPattern();

    [GeneratedRegex("\\\"INNERTUBE_CONTEXT_CLIENT_VERSION\\\"\\s*:\\s*\\\"(?<value>[^\\\"\\\\]{1,100})\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex InnertubeClientVersionPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex YouTubeVideoIdPattern();
}
