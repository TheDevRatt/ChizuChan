using System.Net;
using System.Text.Json;
using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public sealed class SoulseekDownloadStatusReader : ISoulseekDownloadStatusReader
{
    private const int MaximumResponseBytes = 512 * 1024;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SoulseekTrackSearchOptions _options;

    public SoulseekDownloadStatusReader(
        IHttpClientFactory httpClientFactory,
        IOptions<SoulseekTrackSearchOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<StandardResponse<SoulseekDownloadBatchStatusDTO>> GetBatchStatusAsync(
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        if (batchId == Guid.Empty || !_options.Enabled || string.IsNullOrWhiteSpace(_options.ApiKey) ||
            !TryGetLoopbackEndpoint(out var endpoint))
        {
            return Unavailable();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.HttpTimeoutSeconds, 3, 55)));
        try
        {
            var uri = new Uri(endpoint, $"api/v0/transfers/downloads/batches/{batchId:D}");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("X-API-Key", _options.ApiKey.Trim());
            using var response = await _httpClientFactory.CreateClient(nameof(SoulseekDownloadStatusReader))
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return StandardResponse<SoulseekDownloadBatchStatusDTO>.SuccessResponse(
                    new(SoulseekDownloadBatchState.Failed, null, 0, 0, "TransferMissing"), 200);
            }
            if (response.StatusCode != HttpStatusCode.OK)
                return Unavailable((int)response.StatusCode);

            using var document = await ReadBoundedJsonAsync(response.Content, timeout.Token);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) ||
                !idElement.TryGetGuid(out var returnedBatchId) || returnedBatchId != batchId ||
                !root.TryGetProperty("transfers", out var transfers) ||
                transfers.ValueKind != JsonValueKind.Array || transfers.GetArrayLength() != 1)
            {
                return Unavailable(502);
            }

            var transfer = transfers[0];
            if (!TryReadGuid(transfer, "batchId", out var transferBatchId) || transferBatchId != batchId ||
                !TryReadGuid(transfer, "id", out var transferId) ||
                !TryReadText(transfer, "username", 200, out var username) ||
                !TryReadText(transfer, "filename", 500, out var filename) ||
                !TryReadInt64(transfer, "size", out var size) || size <= 0 ||
                !TryReadInt64(transfer, "bytesTransferred", out var bytes) || bytes < 0 || bytes > size)
            {
                return Unavailable(502);
            }

            var stateText = transfer.TryGetProperty("state", out var state)
                ? state.ToString()
                : string.Empty;
            var mapped = MapState(stateText, bytes);
            return StandardResponse<SoulseekDownloadBatchStatusDTO>.SuccessResponse(
                new(mapped.State, transferId, bytes, size, mapped.FailureCategory)
                {
                    Username = username,
                    Filename = filename,
                }, 200);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
            JsonException or InvalidDataException or IOException)
        {
            return Unavailable();
        }
    }

    private bool TryGetLoopbackEndpoint(out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(_options.BaseUrl?.Trim(), UriKind.Absolute, out var configured) ||
            configured.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(configured.UserInfo) ||
            !IPAddress.TryParse(configured.Host, out var address) ||
            !IPAddress.IsLoopback(address))
        {
            return false;
        }
        endpoint = new Uri(configured.AbsoluteUri.TrimEnd('/') + "/");
        return true;
    }

    private static (SoulseekDownloadBatchState State, string? FailureCategory) MapState(
        string state,
        long bytesTransferred)
    {
        if (state.Contains("Succeeded", StringComparison.OrdinalIgnoreCase))
            return (SoulseekDownloadBatchState.Succeeded, null);
        if (state.Contains("Cancelled", StringComparison.OrdinalIgnoreCase))
            return (SoulseekDownloadBatchState.Failed, "TransferCancelled");
        if (state.Contains("TimedOut", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            return (SoulseekDownloadBatchState.Failed, "TransferTimedOut");
        if (state.Contains("Completed", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("Rejected", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("Errored", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            return (SoulseekDownloadBatchState.Failed, "TransferFailed");
        if (bytesTransferred > 0 || state.Contains("InProgress", StringComparison.OrdinalIgnoreCase))
            return (SoulseekDownloadBatchState.Downloading, null);
        return (SoulseekDownloadBatchState.Queued, null);
    }

    private static async Task<JsonDocument> ReadBoundedJsonAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("The Soulseek status response is too large.");
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            if (destination.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("The Soulseek status response is too large.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        destination.Position = 0;
        return await JsonDocument.ParseAsync(destination, cancellationToken: cancellationToken);
    }

    private static bool TryReadGuid(JsonElement root, string name, out Guid value)
    {
        value = default;
        return root.TryGetProperty(name, out var element) && element.TryGetGuid(out value);
    }

    private static bool TryReadInt64(JsonElement root, string name, out long value)
    {
        value = default;
        return root.TryGetProperty(name, out var element) && element.TryGetInt64(out value);
    }

    private static bool TryReadText(JsonElement root, string name, int maximumLength, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength || text.Any(char.IsControl))
            return false;
        value = text;
        return true;
    }

    private static StandardResponse<SoulseekDownloadBatchStatusDTO> Unavailable(int status = 503) =>
        StandardResponse<SoulseekDownloadBatchStatusDTO>.ErrorResponse(
            "Soulseek download status is unavailable right now.", status);
}
