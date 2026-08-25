using System.Xml;
using System.Xml.Linq;
using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public sealed class PlexMusicReadinessReader : IPlexMusicReadinessReader
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DirectMusicRequestStatusOptions _options;

    public PlexMusicReadinessReader(
        IHttpClientFactory httpClientFactory,
        IOptions<DirectMusicRequestStatusOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<StandardResponse<PlexTrackReadinessDTO>> FindTrackAsync(
        DirectMusicRequestStatusDTO request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryGetEndpoint(out var endpoint) || string.IsNullOrWhiteSpace(_options.PlexToken) ||
            _options.PlexMusicSectionId <= 0 || request.ExpectedSize <= 0 ||
            string.IsNullOrWhiteSpace(request.TrackTitle) ||
            string.IsNullOrWhiteSpace(request.RemoteFilename))
        {
            return Unavailable();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.HttpTimeoutSeconds, 3, 60)));
        try
        {
            var relative = $"library/sections/{_options.PlexMusicSectionId}/all" +
                $"?type=10&title={Uri.EscapeDataString(request.TrackTitle.Trim())}";
            using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, relative));
            message.Headers.Add("X-Plex-Token", _options.PlexToken.Trim());
            using var response = await _httpClientFactory.CreateClient(nameof(PlexMusicReadinessReader))
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return Unavailable((int)response.StatusCode);

            var maximumBytes = Math.Clamp(_options.MaxResponseBytes, 4096, 4 * 1024 * 1024);
            await using var stream = await ReadBoundedAsync(response.Content, maximumBytes, timeout.Token);
            using var xmlReader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = maximumBytes,
            });
            var document = XDocument.Load(xmlReader, LoadOptions.None);
            var expectedFilename = FileLeaf(request.RemoteFilename);
            foreach (var track in document.Descendants("Track"))
            {
                if (!EqualAttribute(track, "title", request.TrackTitle))
                    continue;

                var ratingKey = track.Attribute("ratingKey")?.Value;
                if (string.IsNullOrWhiteSpace(ratingKey) ||
                    ratingKey.Length > 50 || ratingKey.Any(character => !char.IsDigit(character)))
                    continue;

                var exactPart = track.Descendants("Part").FirstOrDefault(part =>
                    long.TryParse(part.Attribute("size")?.Value, out var size) &&
                    size == request.ExpectedSize &&
                    string.Equals(
                        FileLeaf(part.Attribute("file")?.Value),
                        expectedFilename,
                        StringComparison.OrdinalIgnoreCase));
                if (exactPart is null)
                    continue;

                return StandardResponse<PlexTrackReadinessDTO>.SuccessResponse(
                    new(true, $"/library/metadata/{ratingKey}"), 200);
            }

            return StandardResponse<PlexTrackReadinessDTO>.SuccessResponse(new(false, null), 200);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
            XmlException or InvalidDataException or IOException)
        {
            return Unavailable();
        }
    }

    private bool TryGetEndpoint(out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(_options.PlexBaseUrl?.Trim(), UriKind.Absolute, out var configured) ||
            configured.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(configured.UserInfo))
            return false;
        endpoint = new Uri(configured.AbsoluteUri.TrimEnd('/') + "/");
        return true;
    }

    private static bool EqualAttribute(XElement element, string name, string expected) =>
        string.Equals(element.Attribute(name)?.Value?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string FileLeaf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        var normalized = path.Trim().Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private static async Task<MemoryStream> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("The Plex response is too large.");
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        var destination = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                if (destination.Length + read > maximumBytes)
                    throw new InvalidDataException("The Plex response is too large.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            destination.Position = 0;
            return destination;
        }
        catch
        {
            await destination.DisposeAsync();
            throw;
        }
    }

    private static StandardResponse<PlexTrackReadinessDTO> Unavailable(int status = 503) =>
        StandardResponse<PlexTrackReadinessDTO>.ErrorResponse(
            "Plex music readiness is unavailable right now.", status);
}
