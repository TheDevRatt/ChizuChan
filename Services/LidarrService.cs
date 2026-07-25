using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public class LidarrService : ILidarrService, ILidarrCompletionReader
{
    private const string ApiKeyHeader = "X-Api-Key";
    private const int MaximumLookupResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly LidarrOptions _options;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ArtistRequestLocks =
        new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public LidarrService(HttpClient httpClient, IOptions<LidarrOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<StandardResponse<IReadOnlyList<LidarrAlbumDTO>>> SearchAlbumsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return StandardResponse<IReadOnlyList<LidarrAlbumDTO>>.ErrorResponse(
                "A search query is required.", (int)HttpStatusCode.BadRequest);
        }

        if (query.Length > _options.MaxQueryLength)
        {
            return StandardResponse<IReadOnlyList<LidarrAlbumDTO>>.ErrorResponse(
                "The search query is too long.", (int)HttpStatusCode.BadRequest);
        }

        var uri = BuildUri($"/api/v1/album/lookup?term={Uri.EscapeDataString(query)}");

        try
        {
            using var request = CreateRequest(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return ApiError<IReadOnlyList<LidarrAlbumDTO>>(response.StatusCode);

            var content = await ReadBoundedContentAsync(response.Content, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var albums = JsonSerializer.Deserialize<List<LidarrAlbumDTO?>>(content, JsonOptions);
            cancellationToken.ThrowIfCancellationRequested();
            if (albums is null)
                return InvalidData<IReadOnlyList<LidarrAlbumDTO>>();

            var validAlbums = albums
                .Where(IsCompleteAlbum)
                .Select(album => album!)
                .ToArray();

            return StandardResponse<IReadOnlyList<LidarrAlbumDTO>>.SuccessResponse(
                validAlbums, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return InvalidData<IReadOnlyList<LidarrAlbumDTO>>();
        }
        catch (InvalidDataException)
        {
            return InvalidData<IReadOnlyList<LidarrAlbumDTO>>();
        }
        catch (HttpRequestException)
        {
            return StandardResponse<IReadOnlyList<LidarrAlbumDTO>>.ErrorResponse(
                "Could not reach Lidarr.", (int)HttpStatusCode.ServiceUnavailable);
        }
        catch (Exception)
        {
            return StandardResponse<IReadOnlyList<LidarrAlbumDTO>>.ErrorResponse(
                "Lidarr search failed.");
        }
    }

    private static async Task<string> ReadBoundedContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumLookupResponseBytes)
            throw new InvalidDataException("Lidarr lookup response exceeded the allowed size.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumLookupResponseBytes)
                throw new InvalidDataException("Lidarr lookup response exceeded the allowed size.");

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    public async Task<StandardResponse<LidarrAlbumRequestResultDTO>> RequestAlbumAsync(
        LidarrAlbumDTO selectedAlbum,
        CancellationToken cancellationToken = default)
    {
        if (!IsCompleteAlbum(selectedAlbum))
        {
            return StandardResponse<LidarrAlbumRequestResultDTO>.ErrorResponse(
                "The selected album is invalid.", (int)HttpStatusCode.BadRequest);
        }

        var foreignAlbumId = selectedAlbum.ForeignAlbumId!;
        var suppliedForeignArtistId = selectedAlbum.Artist!.ForeignArtistId!.Trim();
        var foreignArtistId = Guid.TryParse(suppliedForeignArtistId, out var musicBrainzArtistId)
            ? musicBrainzArtistId.ToString("D")
            : suppliedForeignArtistId;
        var requestLock = ArtistRequestLocks.GetOrAdd(
            foreignArtistId, _ => new SemaphoreSlim(1, 1));
        await requestLock.WaitAsync(cancellationToken);

        try
        {
            var duplicateResult = await FindLocalAlbumAsync(foreignAlbumId, cancellationToken);
            if (!duplicateResult.Success)
                return ForwardError<LidarrAlbumRequestResultDTO, LidarrAlbumDTO?>(duplicateResult);

            if (duplicateResult.Data is not null)
            {
                var ensured = await EnsureAlbumMonitoredAndSearchedAsync(
                    duplicateResult.Data, cancellationToken);
                if (!ensured.Success)
                    return ForwardError<LidarrAlbumRequestResultDTO, bool>(ensured);

                return StandardResponse<LidarrAlbumRequestResultDTO>.SuccessResponse(
                    CreateResult(duplicateResult.Data, selectedAlbum, alreadyExists: true));
            }

            var artistResult = await FindLocalArtistAsync(foreignArtistId, cancellationToken);
            if (!artistResult.Success)
                return ForwardError<LidarrAlbumRequestResultDTO, LidarrArtistDTO?>(artistResult);
            if (artistResult.Data is not null)
            {
                var ensuredArtist = await EnsureArtistMonitoredAsync(artistResult.Data, cancellationToken);
                if (!ensuredArtist.Success)
                    return ForwardError<LidarrAlbumRequestResultDTO, bool>(ensuredArtist);
            }

            var body = CreateAddAlbumBody(selectedAlbum);
            using var request = CreateRequest(HttpMethod.Post, BuildUri("/api/v1/album"));
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var followUp = await FindLocalAlbumAsync(foreignAlbumId, cancellationToken);
                if (!followUp.Success)
                    return ForwardError<LidarrAlbumRequestResultDTO, LidarrAlbumDTO?>(followUp);
                if (followUp.Data is null)
                    return InvalidData<LidarrAlbumRequestResultDTO>();
                var createdAlbum = followUp.Data;

                var ensured = await EnsureAlbumMonitoredAndSearchedAsync(
                    createdAlbum, cancellationToken);
                if (!ensured.Success)
                    return ForwardError<LidarrAlbumRequestResultDTO, bool>(ensured);

                return StandardResponse<LidarrAlbumRequestResultDTO>.SuccessResponse(
                    CreateResult(createdAlbum, selectedAlbum, alreadyExists: false),
                    (int)response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
            {
                var conflictResult = await FindLocalAlbumAsync(foreignAlbumId, cancellationToken);
                if (conflictResult.Success && conflictResult.Data is not null)
                {
                    var ensured = await EnsureAlbumMonitoredAndSearchedAsync(
                        conflictResult.Data, cancellationToken);
                    if (!ensured.Success)
                        return ForwardError<LidarrAlbumRequestResultDTO, bool>(ensured);

                    return StandardResponse<LidarrAlbumRequestResultDTO>.SuccessResponse(
                        CreateResult(conflictResult.Data, selectedAlbum, alreadyExists: true));
                }
            }

            return ApiError<LidarrAlbumRequestResultDTO>(response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return InvalidData<LidarrAlbumRequestResultDTO>();
        }
        catch (InvalidDataException)
        {
            return InvalidData<LidarrAlbumRequestResultDTO>();
        }
        catch (HttpRequestException)
        {
            return StandardResponse<LidarrAlbumRequestResultDTO>.ErrorResponse(
                "Could not reach Lidarr.", (int)HttpStatusCode.ServiceUnavailable);
        }
        catch (Exception)
        {
            return StandardResponse<LidarrAlbumRequestResultDTO>.ErrorResponse(
                "Lidarr request failed.");
        }
        finally
        {
            requestLock.Release();
        }
    }

    private async Task<StandardResponse<bool>> EnsureAlbumMonitoredAndSearchedAsync(
        LidarrAlbumDTO album,
        CancellationToken cancellationToken)
    {
        if (album.Id is not > 0 || album.Artist?.Id is not > 0)
            return InvalidData<bool>();

        var artistResult = await EnsureArtistMonitoredAsync(album.Artist, cancellationToken);
        if (!artistResult.Success)
            return artistResult;

        var albumIds = new JsonArray(album.Id.Value);
        var monitorBody = new JsonObject
        {
            ["albumIds"] = albumIds.DeepClone(),
            ["monitored"] = true,
        }.ToJsonString(JsonOptions);
        using var monitorRequest = CreateRequest(HttpMethod.Put, BuildUri("/api/v1/album/monitor"));
        monitorRequest.Content = new StringContent(monitorBody, Encoding.UTF8, "application/json");
        using var monitorResponse = await _httpClient.SendAsync(
            monitorRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!monitorResponse.IsSuccessStatusCode)
            return ApiError<bool>(monitorResponse.StatusCode);

        var searchBody = new JsonObject
        {
            ["name"] = "AlbumSearch",
            ["albumIds"] = albumIds,
        }.ToJsonString(JsonOptions);
        using var searchRequest = CreateRequest(HttpMethod.Post, BuildUri("/api/v1/command"));
        searchRequest.Content = new StringContent(searchBody, Encoding.UTF8, "application/json");
        using var searchResponse = await _httpClient.SendAsync(
            searchRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!searchResponse.IsSuccessStatusCode)
            return ApiError<bool>(searchResponse.StatusCode);

        return StandardResponse<bool>.SuccessResponse(true, (int)searchResponse.StatusCode);
    }

    private async Task<StandardResponse<bool>> EnsureArtistMonitoredAsync(
        LidarrArtistDTO artist,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["artistIds"] = new JsonArray(artist.Id!.Value),
            ["monitored"] = true,
            ["monitorNewItems"] = "none",
        }.ToJsonString(JsonOptions);
        using var request = CreateRequest(HttpMethod.Put, BuildUri("/api/v1/artist/editor"));
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            return ApiError<bool>(response.StatusCode);

        return StandardResponse<bool>.SuccessResponse(true, (int)response.StatusCode);
    }

    public async Task<StandardResponse<LidarrAlbumCompletionDTO>> GetCompletionAsync(
        int albumId,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (albumId <= 0)
        {
            return StandardResponse<LidarrAlbumCompletionDTO>.ErrorResponse(
                "A positive Lidarr album ID is required.", (int)HttpStatusCode.BadRequest);
        }
        if (requestedAtUtc == default)
        {
            return StandardResponse<LidarrAlbumCompletionDTO>.ErrorResponse(
                "A request timestamp is required.", (int)HttpStatusCode.BadRequest);
        }

        try
        {
            LidarrAlbumCompletionDTO? imported = null;
            var pageSize = Math.Clamp(_options.CompletionHistoryPageSize, 1, 100);
            var historyUri = BuildUri(
                $"/api/v1/history?albumId={albumId}&eventType=8&page=1&pageSize={pageSize}&sortKey=date&sortDirection=descending");
            using (var historyRequest = CreateRequest(HttpMethod.Get, historyUri))
            using (var historyResponse = await _httpClient.SendAsync(
                       historyRequest,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken))
            {
                if (historyResponse.IsSuccessStatusCode)
                {
                    try
                    {
                        var historyJson = await ReadBoundedContentAsync(historyResponse.Content, cancellationToken);
                        imported = ReadDownloadImportedHistory(historyJson, albumId, requestedAtUtc);
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidDataException)
                    {
                        // History is correlation metadata only. Current album statistics remain authoritative.
                    }
                }
            }

            // History can describe files that were later deleted. Always verify current availability.
            using var albumRequest = CreateRequest(HttpMethod.Get, BuildUri($"/api/v1/album/{albumId}"));
            using var albumResponse = await _httpClient.SendAsync(
                albumRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!albumResponse.IsSuccessStatusCode)
                return ApiError<LidarrAlbumCompletionDTO>(albumResponse.StatusCode);

            var albumJson = await ReadBoundedContentAsync(albumResponse.Content, cancellationToken);
            if (!ReadCompleteStatistics(albumJson, albumId))
            {
                return StandardResponse<LidarrAlbumCompletionDTO>.SuccessResponse(
                    new LidarrAlbumCompletionDTO(false, null, null),
                    (int)albumResponse.StatusCode);
            }

            return StandardResponse<LidarrAlbumCompletionDTO>.SuccessResponse(
                imported ?? new LidarrAlbumCompletionDTO(
                    true,
                    HistoryRecordId: null,
                    CompletedAtUtc: DateTimeOffset.UtcNow),
                (int)albumResponse.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return InvalidData<LidarrAlbumCompletionDTO>();
        }
        catch (InvalidDataException)
        {
            return InvalidData<LidarrAlbumCompletionDTO>();
        }
        catch (HttpRequestException)
        {
            return StandardResponse<LidarrAlbumCompletionDTO>.ErrorResponse(
                "Could not reach Lidarr.", (int)HttpStatusCode.ServiceUnavailable);
        }
        catch (Exception)
        {
            return StandardResponse<LidarrAlbumCompletionDTO>.ErrorResponse(
                "Lidarr completion check failed.");
        }
    }

    private static LidarrAlbumCompletionDTO? ReadDownloadImportedHistory(
        string json,
        int albumId,
        DateTimeOffset requestedAtUtc)
    {
        // Lidarr and the bot may differ slightly in wall-clock time. Only imports no more than
        // two minutes before subscription creation are accepted as correlated history.
        var earliestCorrelatedImport = requestedAtUtc.ToUniversalTime().AddMinutes(-2);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("records", out var records) ||
            records.ValueKind != JsonValueKind.Array)
            throw new JsonException("Lidarr history records were missing.");

        foreach (var record in records.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object || !IsDownloadImported(record))
                continue;
            if (record.TryGetProperty("albumId", out var recordAlbumId) &&
                (!recordAlbumId.TryGetInt32(out var parsedAlbumId) || parsedAlbumId != albumId))
                continue;
            if (!record.TryGetProperty("id", out var idElement) ||
                !idElement.TryGetInt32(out var historyId) || historyId <= 0)
                continue;
            if (!record.TryGetProperty("date", out var dateElement) ||
                dateElement.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(dateElement.GetString(), out var completedAt))
                continue;

            var completedAtUtc = completedAt.ToUniversalTime();
            if (completedAtUtc < earliestCorrelatedImport)
                continue;

            return new LidarrAlbumCompletionDTO(true, historyId, completedAtUtc);
        }

        return null;
    }

    private static bool IsDownloadImported(JsonElement record)
    {
        if (!record.TryGetProperty("eventType", out var eventType))
            return false;
        if (eventType.ValueKind == JsonValueKind.Number)
            return eventType.TryGetInt32(out var value) && value == 8;
        if (eventType.ValueKind != JsonValueKind.String)
            return false;

        var valueText = eventType.GetString();
        return string.Equals(valueText, "downloadImported", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(valueText, "8", StringComparison.Ordinal);
    }

    private static bool ReadCompleteStatistics(string json, int albumId)
    {
        using var document = JsonDocument.Parse(json);
        var album = document.RootElement;
        if (album.ValueKind != JsonValueKind.Object)
            throw new JsonException("Lidarr album data was invalid.");
        if (album.TryGetProperty("id", out var idElement) &&
            (!idElement.TryGetInt32(out var parsedId) || parsedId != albumId))
            throw new JsonException("Lidarr returned the wrong album.");
        if (!album.TryGetProperty("statistics", out var statistics) ||
            statistics.ValueKind != JsonValueKind.Object)
            return false;
        if (!statistics.TryGetProperty("trackCount", out var trackCountElement) ||
            !trackCountElement.TryGetInt32(out var trackCount) ||
            !statistics.TryGetProperty("trackFileCount", out var trackFileCountElement) ||
            !trackFileCountElement.TryGetInt32(out var trackFileCount))
            return false;

        return trackCount > 0 && trackFileCount >= trackCount;
    }

    private async Task<StandardResponse<LidarrAlbumDTO?>> FindLocalAlbumAsync(
        string foreignAlbumId,
        CancellationToken cancellationToken)
    {
        var uri = BuildUri(
            $"/api/v1/album?foreignAlbumId={Uri.EscapeDataString(foreignAlbumId)}");
        using var request = CreateRequest(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            return ApiError<LidarrAlbumDTO?>(response.StatusCode);

        var content = await ReadBoundedContentAsync(response.Content, cancellationToken);
        var albums = JsonSerializer.Deserialize<List<LidarrAlbumDTO?>>(content, JsonOptions);
        cancellationToken.ThrowIfCancellationRequested();
        if (albums is null)
            return InvalidData<LidarrAlbumDTO?>();

        var matchingAlbums = albums
            .Where(album => album is not null &&
                string.Equals(album.ForeignAlbumId, foreignAlbumId, StringComparison.Ordinal))
            .ToArray();
        if (matchingAlbums.Length == 0)
            return StandardResponse<LidarrAlbumDTO?>.SuccessResponse(null, (int)response.StatusCode);

        var authoritative = matchingAlbums.FirstOrDefault(IsAuthoritativeAlbum);
        if (authoritative is null)
            return InvalidData<LidarrAlbumDTO?>();

        return StandardResponse<LidarrAlbumDTO?>.SuccessResponse(
            authoritative, (int)response.StatusCode);
    }

    private async Task<StandardResponse<LidarrArtistDTO?>> FindLocalArtistAsync(
        string foreignArtistId,
        CancellationToken cancellationToken)
    {
        var uri = BuildUri(
            $"/api/v1/artist?mbId={Uri.EscapeDataString(foreignArtistId)}");
        using var request = CreateRequest(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            return ApiError<LidarrArtistDTO?>(response.StatusCode);

        var content = await ReadBoundedContentAsync(response.Content, cancellationToken);
        var artists = JsonSerializer.Deserialize<List<LidarrArtistDTO?>>(content, JsonOptions);
        cancellationToken.ThrowIfCancellationRequested();
        if (artists is null)
            return InvalidData<LidarrArtistDTO?>();

        var matchingArtists = artists
            .Where(artist => artist is not null &&
                string.Equals(artist.ForeignArtistId, foreignArtistId, StringComparison.Ordinal))
            .ToArray();
        if (matchingArtists.Length == 0)
            return StandardResponse<LidarrArtistDTO?>.SuccessResponse(null, (int)response.StatusCode);

        var authoritative = matchingArtists.FirstOrDefault(IsAuthoritativeArtist);
        if (authoritative is null)
            return InvalidData<LidarrArtistDTO?>();

        return StandardResponse<LidarrArtistDTO?>.SuccessResponse(
            authoritative, (int)response.StatusCode);
    }

    private static bool IsCompleteAlbum(LidarrAlbumDTO? album) =>
        album is not null &&
        !string.IsNullOrWhiteSpace(album.ForeignAlbumId) &&
        album.Artist is not null &&
        !string.IsNullOrWhiteSpace(album.Artist.ForeignArtistId);

    private static bool IsAuthoritativeAlbum(LidarrAlbumDTO? album) =>
        album is not null &&
        album.Id is > 0 &&
        !string.IsNullOrWhiteSpace(album.ForeignAlbumId);

    private static bool IsAuthoritativeArtist(LidarrArtistDTO? artist) =>
        artist is not null &&
        artist.Id is > 0 &&
        !string.IsNullOrWhiteSpace(artist.ForeignArtistId);

    private string CreateAddAlbumBody(LidarrAlbumDTO selectedAlbum)
    {
        var album = JsonSerializer.SerializeToNode(selectedAlbum, JsonOptions)!.AsObject();
        // Lookup IDs are not Lidarr database identities and must not be sent back as authoritative IDs.
        album.Remove("id");
        album["monitored"] = true;
        album["anyReleaseOk"] = true;
        album["addOptions"] = new JsonObject
        {
            ["addType"] = "manual",
            ["searchForNewAlbum"] = false
        };

        var artist = album["artist"]!.AsObject();
        artist.Remove("id");
        artist["qualityProfileId"] = _options.QualityProfileId;
        artist["metadataProfileId"] = _options.MetadataProfileId;
        artist["rootFolderPath"] = _options.RootFolderPath;
        artist["monitored"] = true;
        artist["monitorNewItems"] = "none";
        artist.Remove("addOptions");

        return album.ToJsonString(JsonOptions);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.ApiKey);
        return request;
    }

    private string BuildUri(string relativePath) =>
        $"{_options.BaseUrl.TrimEnd('/')}{relativePath}";

    private static LidarrAlbumRequestResultDTO CreateResult(
        LidarrAlbumDTO authoritativeAlbum,
        LidarrAlbumDTO selectedAlbum,
        bool alreadyExists) => new()
    {
        AlbumId = authoritativeAlbum.Id!.Value,
        ForeignAlbumId = authoritativeAlbum.ForeignAlbumId!,
        Title = authoritativeAlbum.Title ?? selectedAlbum.Title ?? "Unknown album",
        ArtistName = authoritativeAlbum.Artist?.ArtistName ??
            selectedAlbum.Artist?.ArtistName ?? "Unknown artist",
        AlreadyExists = alreadyExists
    };

    private static StandardResponse<TTarget> ForwardError<TTarget, TSource>(
        StandardResponse<TSource> source) =>
        StandardResponse<TTarget>.ErrorResponse(
            source.ErrorMessage ?? "Lidarr request failed.", source.StatusCode);

    private static StandardResponse<T> ApiError<T>(HttpStatusCode statusCode) =>
        StandardResponse<T>.ErrorResponse("Lidarr request failed.", (int)statusCode);

    private static StandardResponse<T> InvalidData<T>() =>
        StandardResponse<T>.ErrorResponse("Lidarr returned invalid data.");
}
