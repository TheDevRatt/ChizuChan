using System.Buffers;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public sealed partial class SoulseekTrackSearchService : ISoulseekTrackSearchService
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".alac", ".wav", ".aiff", ".aif", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wma",
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SoulseekTrackSearchOptions _options;

    public SoulseekTrackSearchService(
        IHttpClientFactory httpClientFactory,
        IOptions<SoulseekTrackSearchOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<StandardResponse<IReadOnlyList<SoulseekTrackSuggestionDTO>>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (!TryGetEndpoint(out var endpoint) || string.IsNullOrWhiteSpace(_options.ApiKey))
            return SearchUnavailable();
        if (string.IsNullOrWhiteSpace(query))
            return StandardResponse<IReadOnlyList<SoulseekTrackSuggestionDTO>>.ErrorResponse("A search query is required.", 400);

        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length > Math.Clamp(_options.MaxQueryLength, 1, 200))
            return StandardResponse<IReadOnlyList<SoulseekTrackSuggestionDTO>>.ErrorResponse("The search query is too long.", 400);

        var searchId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/api/v0/searches"));
        request.Headers.TryAddWithoutValidation("X-API-Key", _options.ApiKey);
        request.Content = JsonContent.Create(new
        {
            id = searchId,
            searchText = trimmedQuery,
            searchTimeout = _options.GetSlskdSearchTimeout(),
            responseLimit = Math.Clamp(_options.ResponseLimit, 1, 100),
            fileLimit = Math.Clamp(_options.FileLimit, 1, 500),
            filterResponses = true,
        });

        var preserveServerSearch = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.GetEffectiveHttpTimeoutSeconds()));
            using var client = _httpClientFactory.CreateClient(nameof(SoulseekTrackSearchService));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return SearchUnavailable((int)response.StatusCode);

            using var created = await ReadBoundedJsonAsync(response.Content, timeout.Token);
            JsonDocument? completed = null;
            var document = created;
            if (RequiresPolling(created.RootElement, searchId))
            {
                completed = await PollSearchAsync(client, endpoint, searchId, timeout.Token);
                if (completed is null)
                    return SearchUnavailable();
                document = completed;
            }

            var limit = Math.Clamp(_options.ResultLimit, 1, 5);
            try
            {
                var result = StandardResponse<IReadOnlyList<SoulseekTrackSuggestionDTO>>.SuccessResponse(
                    ParseResults(document.RootElement, searchId, trimmedQuery, limit));
                preserveServerSearch = true;
                return result;
            }
            finally
            {
                completed?.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return SearchUnavailable();
        }
        finally
        {
            if (!preserveServerSearch)
                await TryCancelSearchAsync(endpoint, searchId);
        }
    }

    private async Task TryCancelSearchAsync(Uri endpoint, Guid searchId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                new Uri(endpoint, $"/api/v0/searches/{searchId:D}"));
            request.Headers.TryAddWithoutValidation("X-API-Key", _options.ApiKey);
            using var client = _httpClientFactory.CreateClient(nameof(SoulseekTrackSearchService));
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
        }
        catch (Exception)
        {
            // Best-effort cleanup must not replace the original search result or cancellation.
        }
    }

    private async Task<JsonDocument?> PollSearchAsync(
        HttpClient client,
        Uri endpoint,
        Guid searchId,
        CancellationToken cancellationToken)
    {
        var pollUri = new Uri(endpoint, $"/api/v0/searches/{searchId:D}?includeResponses=true");
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, pollUri);
            request.Headers.TryAddWithoutValidation("X-API-Key", _options.ApiKey);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var document = await ReadBoundedJsonAsync(response.Content, cancellationToken);
            if (IsFinalized(document.RootElement))
                return document;

            document.Dispose();
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static bool RequiresPolling(JsonElement root, Guid searchId)
    {
        if (!root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
            return false;
        if (root.TryGetProperty("id", out var id) &&
            (!id.TryGetGuid(out var returnedId) || returnedId != searchId))
        {
            throw new JsonException("slskd returned an unexpected search identifier.");
        }
        return !IsFinalized(root);
    }

    private static bool IsFinalized(JsonElement root) =>
        root.TryGetProperty("state", out var state) &&
        state.ValueKind == JsonValueKind.String &&
        state.GetString()!.Contains("Completed", StringComparison.OrdinalIgnoreCase) &&
        root.TryGetProperty("endedAt", out var endedAt) &&
        endedAt.ValueKind == JsonValueKind.String &&
        endedAt.TryGetDateTimeOffset(out _);

    public async Task<StandardResponse<bool>> QueueDownloadAsync(
        SoulseekTrackSearchResult track,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!TryGetEndpoint(out var endpoint) || string.IsNullOrWhiteSpace(_options.ApiKey) ||
            track.SearchId == Guid.Empty || !IsSafeUsername(track.Username) ||
            !IsSafeFilename(track.Filename) || track.Size <= 0)
        {
            return QueueUnavailable();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(endpoint, "/api/v0/transfers/downloads/batches"));
        request.Headers.TryAddWithoutValidation("X-API-Key", _options.ApiKey);
        request.Content = JsonContent.Create(new
        {
            username = track.Username,
            searchId = track.SearchId,
            files = new[] { new { filename = track.Filename, size = track.Size } },
            options = new { },
        });

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.HttpTimeoutSeconds, 5, 60)));
            using var response = await _httpClientFactory.CreateClient(nameof(SoulseekTrackSearchService))
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Created)
                return StandardResponse<bool>.SuccessResponse(true, (int)response.StatusCode);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.MultiStatus)
            {
                using var document = await ReadBoundedJsonAsync(response.Content, timeout.Token);
                if (document.RootElement.TryGetProperty("failures", out var failures) &&
                    failures.ValueKind == JsonValueKind.Array && failures.GetArrayLength() == 0)
                {
                    return StandardResponse<bool>.SuccessResponse(true, (int)response.StatusCode);
                }
            }

            return QueueUnavailable((int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return QueueUnavailable();
        }
    }

    private bool TryGetEndpoint(out Uri endpoint)
    {
        endpoint = null!;
        if (!_options.Enabled || !Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        if (!parsed.IsLoopback)
            return false;

        endpoint = parsed;
        return true;
    }

    private static async Task<JsonDocument> ReadBoundedJsonAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("The slskd response exceeded the permitted size.");

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken);
                if (read == 0)
                    break;
                if (buffer.Length + read > MaximumResponseBytes)
                    throw new InvalidDataException("The slskd response exceeded the permitted size.");
                await buffer.WriteAsync(rented.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
    }

    private static IReadOnlyList<SoulseekTrackSuggestionDTO> ParseResults(
        JsonElement root,
        Guid searchId,
        string query,
        int limit)
    {
        if (!root.TryGetProperty("responses", out var responses) || responses.ValueKind != JsonValueKind.Array)
            return [];

        var queryTokens = TokenPattern().Matches(query.ToLowerInvariant()).Select(match => match.Value).Distinct().ToArray();
        var candidates = new List<Candidate>();
        foreach (var response in responses.EnumerateArray())
        {
            if (response.ValueKind != JsonValueKind.Object)
                continue;
            var username = GetString(response, "username");
            if (!IsSafeUsername(username))
                continue;

            var freeSlot = GetBool(response, "hasFreeUploadSlot");
            var uploadSpeed = GetInt32(response, "uploadSpeed");
            var queueLength = GetInt64(response, "queueLength");
            if (!response.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var file in files.EnumerateArray())
            {
                if (!TryParseFile(file, out var parsed))
                    continue;
                var lowerFilename = parsed.Filename.ToLowerInvariant();
                var relevance = queryTokens.Count(lowerFilename.Contains);
                candidates.Add(new Candidate(
                    new SoulseekTrackSuggestionDTO
                    {
                        SearchId = searchId,
                        Username = username!,
                        Filename = parsed.Filename,
                        Size = parsed.Size,
                        Title = parsed.Title,
                        Artist = parsed.Artist,
                        Duration = TimeSpan.FromSeconds(parsed.Length),
                        Quality = parsed.Quality,
                        HasFreeUploadSlot = freeSlot,
                        UploadSpeed = uploadSpeed,
                        QueueLength = queueLength,
                    },
                    parsed.QualityRank,
                    relevance,
                    parsed.BitDepth,
                    parsed.SampleRate,
                    parsed.BitRate));
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.QualityRank)
            .ThenByDescending(candidate => candidate.BitDepth)
            .ThenByDescending(candidate => candidate.SampleRate)
            .ThenByDescending(candidate => candidate.BitRate)
            .ThenByDescending(candidate => candidate.Relevance)
            .ThenByDescending(candidate => candidate.Track.HasFreeUploadSlot)
            .ThenByDescending(candidate => candidate.Track.UploadSpeed)
            .ThenBy(candidate => candidate.Track.QueueLength)
            .ThenBy(candidate => candidate.Track.Filename, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Track.Username, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(candidate => $"{Leaf(candidate.Track.Filename).ToLowerInvariant()}|{candidate.Track.Size}")
            .Take(limit)
            .Select(candidate => candidate.Track)
            .ToArray();
    }

    private static bool TryParseFile(JsonElement file, out ParsedFile parsed)
    {
        parsed = default;
        if (file.ValueKind != JsonValueKind.Object || GetBool(file, "isLocked"))
            return false;

        var filename = GetString(file, "filename");
        var size = GetInt64(file, "size");
        var length = GetInt32(file, "length");
        if (!IsSafeFilename(filename) || size <= 0 || length is < 30 or > 1200)
            return false;

        var leaf = Leaf(filename!);
        var extension = Path.GetExtension(leaf);
        if (!SupportedExtensions.Contains(extension))
            return false;

        var stem = Path.GetFileNameWithoutExtension(leaf).Trim();
        if (string.IsNullOrWhiteSpace(stem))
            return false;
        var split = stem.Split(" - ", 2, StringSplitOptions.TrimEntries);
        var artist = split.Length == 2 ? Bound(split[0], 120) : null;
        var title = Bound(split.Length == 2 ? split[1] : stem, 200);
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var bitRate = Math.Max(0, GetInt32(file, "bitRate"));
        var sampleRate = Math.Max(0, GetInt32(file, "sampleRate"));
        var bitDepth = Math.Max(0, GetInt32(file, "bitDepth"));
        var qualityRank = extension.ToLowerInvariant() switch
        {
            ".flac" or ".alac" or ".wav" or ".aiff" or ".aif" => 3,
            ".m4a" or ".aac" or ".ogg" or ".opus" => 2,
            _ => 1,
        };
        var qualityParts = new List<string> { extension.TrimStart('.').ToUpperInvariant() };
        if (bitDepth > 0)
            qualityParts.Add($"{bitDepth}-bit");
        if (sampleRate > 0)
            qualityParts.Add($"{sampleRate / 1000d:0.#} kHz");
        if (bitRate > 0 && qualityRank < 3)
            qualityParts.Add($"{bitRate} kbps");

        parsed = new ParsedFile(
            filename!, size, length, title, artist, string.Join(" / ", qualityParts),
            qualityRank, bitDepth, sampleRate, bitRate);
        return true;
    }

    private static bool IsSafeUsername(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 500 && !value.Any(char.IsControl);

    private static bool IsSafeFilename(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1000 || value.Any(char.IsControl) ||
            value.StartsWith('/') || value.StartsWith('\\'))
        {
            return false;
        }

        return !value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    private static string Leaf(string filename) => filename.Split(['/', '\\']).Last();
    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;
    private static int GetInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : 0;
    private static long GetInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : 0;

    private static StandardResponse<IReadOnlyList<SoulseekTrackSuggestionDTO>> SearchUnavailable(int statusCode = 503) =>
        StandardResponse<IReadOnlyList<SoulseekTrackSuggestionDTO>>.ErrorResponse(
            "Soulseek search is unavailable right now.", statusCode);
    private static StandardResponse<bool> QueueUnavailable(int statusCode = 503) =>
        StandardResponse<bool>.ErrorResponse("Couldn't queue that Soulseek track right now.", statusCode);

    private readonly record struct ParsedFile(
        string Filename, long Size, int Length, string Title, string? Artist, string Quality,
        int QualityRank, int BitDepth, int SampleRate, int BitRate);
    private readonly record struct Candidate(
        SoulseekTrackSuggestionDTO Track, int QualityRank, int Relevance, int BitDepth, int SampleRate, int BitRate);

    [GeneratedRegex("[a-z0-9]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TokenPattern();
}
