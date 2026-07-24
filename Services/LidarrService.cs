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

public class LidarrService : ILidarrService
{
    private const string ApiKeyHeader = "X-Api-Key";
    private const int MaximumLookupResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly LidarrOptions _options;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AlbumRequestLocks =
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
        LidarrAlbumDTO selectedAlbum)
    {
        if (!IsCompleteAlbum(selectedAlbum))
        {
            return StandardResponse<LidarrAlbumRequestResultDTO>.ErrorResponse(
                "The selected album is invalid.", (int)HttpStatusCode.BadRequest);
        }

        var requestLock = AlbumRequestLocks.GetOrAdd(
            selectedAlbum.ForeignAlbumId!, _ => new SemaphoreSlim(1, 1));
        await requestLock.WaitAsync();

        try
        {
            var duplicateResult = await CheckForDuplicateAsync(selectedAlbum.ForeignAlbumId!);
            if (!duplicateResult.Success)
            {
                return StandardResponse<LidarrAlbumRequestResultDTO>.ErrorResponse(
                    duplicateResult.ErrorMessage ?? "Lidarr request failed.", duplicateResult.StatusCode);
            }

            if (duplicateResult.Data)
            {
                return StandardResponse<LidarrAlbumRequestResultDTO>.SuccessResponse(
                    CreateResult(selectedAlbum, alreadyExists: true));
            }

            var body = CreateAddAlbumBody(selectedAlbum);
            using var request = CreateRequest(HttpMethod.Post, BuildUri("/api/v1/album"));
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return StandardResponse<LidarrAlbumRequestResultDTO>.SuccessResponse(
                    CreateResult(selectedAlbum, alreadyExists: false), (int)response.StatusCode);
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
            {
                var conflictDuplicateResult = await CheckForDuplicateAsync(selectedAlbum.ForeignAlbumId!);
                if (conflictDuplicateResult.Success && conflictDuplicateResult.Data)
                {
                    return StandardResponse<LidarrAlbumRequestResultDTO>.SuccessResponse(
                        CreateResult(selectedAlbum, alreadyExists: true));
                }
            }

            return ApiError<LidarrAlbumRequestResultDTO>(response.StatusCode);
        }
        catch (JsonException)
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

    private async Task<StandardResponse<bool>> CheckForDuplicateAsync(string foreignAlbumId)
    {
        var uri = BuildUri(
            $"/api/v1/album?foreignAlbumId={Uri.EscapeDataString(foreignAlbumId)}");
        using var request = CreateRequest(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return ApiError<bool>(response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var albums = JsonSerializer.Deserialize<List<LidarrAlbumDTO?>>(content, JsonOptions);
        if (albums is null)
            return InvalidData<bool>();

        return StandardResponse<bool>.SuccessResponse(
            albums.Any(album => album is not null), (int)response.StatusCode);
    }

    private static bool IsCompleteAlbum(LidarrAlbumDTO? album) =>
        album is not null &&
        !string.IsNullOrWhiteSpace(album.ForeignAlbumId) &&
        album.Artist is not null &&
        !string.IsNullOrWhiteSpace(album.Artist.ForeignArtistId);

    private string CreateAddAlbumBody(LidarrAlbumDTO selectedAlbum)
    {
        var album = JsonSerializer.SerializeToNode(selectedAlbum, JsonOptions)!.AsObject();
        album["monitored"] = true;
        album["anyReleaseOk"] = true;
        album["addOptions"] = new JsonObject
        {
            ["addType"] = "manual",
            ["searchForNewAlbum"] = true
        };

        var artist = album["artist"]!.AsObject();
        artist["qualityProfileId"] = _options.QualityProfileId;
        artist["metadataProfileId"] = _options.MetadataProfileId;
        artist["rootFolderPath"] = _options.RootFolderPath;
        artist["monitored"] = false;
        artist["monitorNewItems"] = "none";
        artist["addOptions"] = new JsonObject
        {
            ["monitor"] = "none",
            ["albumsToMonitor"] = new JsonArray(),
            ["monitored"] = false,
            ["searchForMissingAlbums"] = false
        };

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
        LidarrAlbumDTO album,
        bool alreadyExists) => new()
    {
        Title = album.Title ?? "Unknown album",
        ArtistName = album.Artist?.ArtistName ?? "Unknown artist",
        AlreadyExists = alreadyExists
    };

    private static StandardResponse<T> ApiError<T>(HttpStatusCode statusCode) =>
        StandardResponse<T>.ErrorResponse("Lidarr request failed.", (int)statusCode);

    private static StandardResponse<T> InvalidData<T>() =>
        StandardResponse<T>.ErrorResponse("Lidarr returned invalid data.");
}
