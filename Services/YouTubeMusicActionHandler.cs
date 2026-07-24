using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public sealed partial class YouTubeMusicActionHandler : IYouTubeMusicActionHandler
{
    private const int MaximumToolErrorCharacters = 32 * 1024;
    private readonly IYouTubeDownloadTool _tool;
    private readonly YouTubeMusicDownloadOptions _options;
    private readonly ILogger<YouTubeMusicActionHandler> _logger;
    private readonly object _gateSync = new();
    private readonly Dictionary<string, VideoLockState> _videoLocks = new(StringComparer.Ordinal);

    public YouTubeMusicActionHandler(
        IYouTubeDownloadTool tool,
        IOptions<YouTubeMusicDownloadOptions> options,
        ILogger<YouTubeMusicActionHandler> logger)
    {
        _tool = tool;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<YouTubeMusicActionResult> HandleAsync(
        ulong userId,
        string canonicalVideoId,
        CancellationToken cancellationToken = default)
    {
        _ = userId; // The narrow action seam intentionally never consumes Discord-supplied metadata.
        if (canonicalVideoId is null || !YouTubeVideoIdPattern().IsMatch(canonicalVideoId))
            return YouTubeMusicActionResult.Failed("That YouTube track selection is invalid.");
        if (!_options.Enabled)
            return YouTubeMusicActionResult.Failed("YouTube downloads are not configured.");

        var state = AcquireVideoLock(canonicalVideoId);
        var entered = false;
        try
        {
            await state.Gate.WaitAsync(cancellationToken);
            entered = true;
            if (state.Completed is { } completed &&
                IsValidExistingFile(completed.Path, _options.GetMaxFileSizeBytes()))
            {
                return AlreadyDownloaded(completed.Artist, completed.Title);
            }

            return await DownloadCoreAsync(canonicalVideoId, state, cancellationToken);
        }
        finally
        {
            if (entered)
                state.Gate.Release();
            ReleaseVideoLock(canonicalVideoId, state);
        }
    }

    private async Task<YouTubeMusicActionResult> DownloadCoreAsync(
        string videoId,
        VideoLockState state,
        CancellationToken cancellationToken)
    {
        string root;
        string ytDlp;
        string ffmpeg;
        try
        {
            root = ResolveLibraryRoot(_options.LibraryRootPath);
            ytDlp = ResolveExecutable(_options.YtDlpPath, "yt-dlp.exe", "yt-dlp");
            ffmpeg = ResolveExecutable(_options.FfmpegPath, "ffmpeg.exe", "ffmpeg");
            Directory.CreateDirectory(root);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSafe(exception, "configuration");
            return YouTubeMusicActionResult.Failed("YouTube downloads are not configured correctly.");
        }

        var stagingRoot = Path.Combine(root, ".chizu-staging");
        var stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            var canonicalUrl = $"https://www.youtube.com/watch?v={videoId}";
            var timeout = TimeSpan.FromSeconds(_options.GetDownloadTimeoutSeconds());
            var metadataLimit = _options.GetMaxMetadataBytes();
            var probeArguments = new[]
            {
                "--ignore-config",
                "--no-playlist",
                "--skip-download",
                "--dump-single-json",
                "--no-warnings",
                canonicalUrl,
            };
            var probe = await _tool.RunAsync(new YouTubeDownloadToolInvocation(
                ytDlp,
                probeArguments,
                stagingDirectory,
                timeout,
                metadataLimit,
                MaximumToolErrorCharacters), cancellationToken);
            if (probe.ExitCode != 0)
                return SafeFailure();

            if (!TryParseMetadata(probe.StandardOutput, videoId, _options, out var metadata, out var rejection))
                return YouTubeMusicActionResult.Failed(rejection);

            var destination = YouTubeMusicPathPolicy.BuildDestinationPath(
                root, metadata.Artist, metadata.Album, metadata.Title, videoId);
            if (File.Exists(destination))
            {
                if (!IsValidExistingFile(destination, _options.GetMaxFileSizeBytes()))
                    return YouTubeMusicActionResult.Failed("A conflicting library item already exists for that track.");
                state.Completed = new(destination, metadata.Artist, metadata.Title);
                return AlreadyDownloaded(metadata.Artist, metadata.Title);
            }

            var maxFileSize = _options.GetMaxFileSizeBytes();
            var downloadArguments = BuildDownloadArguments(
                canonicalUrl,
                stagingDirectory,
                ffmpeg,
                _options.GetMaxDurationSeconds(),
                maxFileSize);
            var download = await _tool.RunAsync(new YouTubeDownloadToolInvocation(
                ytDlp,
                downloadArguments,
                stagingDirectory,
                timeout,
                64 * 1024,
                MaximumToolErrorCharacters), cancellationToken);
            if (download.ExitCode != 0)
                return SafeFailure();

            var audioPath = Path.Combine(stagingDirectory, "download.m4a");
            if (!IsValidExistingFile(audioPath, maxFileSize))
                return SafeFailure();

            var coverPath = FindCover(stagingDirectory, maxFileSize);
            var finalStagedPath = Path.Combine(stagingDirectory, $"final-{Guid.NewGuid():N}.m4a");
            var ffmpegArguments = BuildFfmpegArguments(
                audioPath, coverPath, finalStagedPath, metadata, canonicalUrl);
            var tag = await _tool.RunAsync(new YouTubeDownloadToolInvocation(
                ffmpeg,
                ffmpegArguments,
                stagingDirectory,
                timeout,
                64 * 1024,
                MaximumToolErrorCharacters), cancellationToken);
            if (tag.ExitCode != 0 || !IsValidExistingFile(finalStagedPath, maxFileSize))
                return SafeFailure();

            var destinationDirectory = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("Destination has no parent directory.");
            Directory.CreateDirectory(destinationDirectory);
            if (File.Exists(destination))
            {
                state.Completed = new(destination, metadata.Artist, metadata.Title);
                return AlreadyDownloaded(metadata.Artist, metadata.Title);
            }

            // staging and destination are deliberately below the same configured root, so this
            // non-overwriting move is an atomic same-volume promotion.
            File.Move(finalStagedPath, destination, overwrite: false);
            state.Completed = new(destination, metadata.Artist, metadata.Title);
            return YouTubeMusicActionResult.Succeeded(
                $"Downloaded **{Limit(metadata.Artist, 70)} — {Limit(metadata.Title, 70)}**. " +
                "Plex/Plexamp will pick it up on the next library scan.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogSafe(exception, "download");
            return SafeFailure();
        }
        finally
        {
            DeleteDirectoryBestEffort(stagingDirectory);
            DeleteIfEmptyBestEffort(stagingRoot);
        }
    }

    public static IReadOnlyList<string> BuildDownloadArguments(
        string canonicalUrl,
        string stagingDirectory,
        string ffmpegPath,
        int maxDurationSeconds,
        long maxFileSizeBytes) =>
    [
        "--ignore-config",
        "--no-playlist",
        "--max-downloads", "1",
        "--match-filter", $"!is_live & !was_live & duration <= {maxDurationSeconds}",
        "--max-filesize", maxFileSizeBytes.ToString(CultureInfo.InvariantCulture),
        "--extract-audio",
        "--audio-format", "m4a",
        "--audio-quality", "0",
        "--write-thumbnail",
        "--convert-thumbnails", "jpg",
        "--ffmpeg-location", ffmpegPath,
        "--output", Path.Combine(stagingDirectory, "download.%(ext)s"),
        canonicalUrl,
    ];

    public static IReadOnlyList<string> BuildFfmpegArguments(
        string audioPath,
        string? coverPath,
        string outputPath,
        YouTubeTrackMetadata metadata,
        string canonicalUrl)
    {
        var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-i", audioPath };
        if (coverPath is not null)
            arguments.AddRange(["-i", coverPath]);

        arguments.AddRange(["-map", "0:a:0"]);
        if (coverPath is not null)
        {
            arguments.AddRange([
                "-map", "1:v:0",
                "-c:v", "mjpeg",
                "-disposition:v:0", "attached_pic",
            ]);
        }
        arguments.AddRange([
            "-c:a", "aac",
            "-b:a", "256k",
            "-movflags", "+faststart",
            "-metadata", $"title={metadata.Title}",
            "-metadata", $"artist={metadata.Artist}",
            "-metadata", $"album_artist={metadata.Artist}",
            "-metadata", $"album={metadata.Album}",
            "-metadata", $"date={metadata.Date ?? ""}",
            "-metadata", $"year={metadata.Year?.ToString(CultureInfo.InvariantCulture) ?? ""}",
            "-metadata", "track=1/1",
            "-metadata", "disc=1/1",
            "-metadata", $"genre={metadata.Genre}",
            "-metadata", $"comment=Source: {canonicalUrl}",
            "-metadata", $"source={canonicalUrl}",
            "-metadata", "release_type=single",
            "-metadata", "releasetype=single",
            outputPath,
        ]);
        return arguments;
    }

    public static bool TryParseMetadata(
        string json,
        string expectedVideoId,
        YouTubeMusicDownloadOptions options,
        out YouTubeTrackMetadata metadata,
        out string rejectionMessage)
    {
        metadata = default!;
        rejectionMessage = "YouTube returned invalid track information.";
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 16,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            var resultType = GetString(root, "_type");
            if (resultType is not null && !string.Equals(resultType, "video", StringComparison.OrdinalIgnoreCase))
                return false;
            if (root.TryGetProperty("entries", out _))
                return false;
            var extractor = GetString(root, "extractor");
            if (!string.Equals(extractor, "youtube", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(GetString(root, "id"), expectedVideoId, StringComparison.Ordinal))
                return false;
            if (IsLiveOrPremiere(root))
            {
                rejectionMessage = "Live streams and premieres can't be downloaded.";
                return false;
            }
            if (!TryGetDuration(root, out var duration) || duration > options.GetMaxDurationSeconds())
            {
                rejectionMessage = "That track is too long to download.";
                return false;
            }

            var rawTitle = FirstNonEmpty(GetString(root, "track"), GetString(root, "title"), options.FallbackTitle);
            var rawArtist = FirstNonEmpty(
                GetString(root, "artist"), GetString(root, "uploader"), GetString(root, "channel"), options.FallbackArtist);
            var rawAlbum = FirstNonEmpty(GetString(root, "album"), GetString(root, "track"), GetString(root, "title"), options.FallbackAlbum);
            var rawGenre = FirstNonEmpty(GetString(root, "genre"), options.Genre, "YouTube");
            var (year, date) = ParseDate(root);
            metadata = new YouTubeTrackMetadata(
                YouTubeMusicPathPolicy.SanitizeSegment(rawTitle, "Untitled"),
                YouTubeMusicPathPolicy.SanitizeSegment(rawArtist, "Unknown Artist"),
                YouTubeMusicPathPolicy.SanitizeSegment(rawAlbum, "Single"),
                YouTubeMusicPathPolicy.SanitizeSegment(rawGenre, "YouTube"),
                year,
                date,
                duration);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetDuration(JsonElement root, out double duration)
    {
        duration = 0;
        return root.TryGetProperty("duration", out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out duration) &&
               double.IsFinite(duration) && duration > 0;
    }

    private static bool IsLiveOrPremiere(JsonElement root)
    {
        if (root.TryGetProperty("is_live", out var isLive) && isLive.ValueKind == JsonValueKind.True)
            return true;
        var status = GetString(root, "live_status");
        return !string.IsNullOrWhiteSpace(status) &&
               !string.Equals(status, "not_live", StringComparison.OrdinalIgnoreCase);
    }

    private static (int? Year, string? Date) ParseDate(JsonElement root)
    {
        if (TryGetYear(root, "release_year", out var releaseYear))
            return (releaseYear, releaseYear.ToString(CultureInfo.InvariantCulture));
        foreach (var propertyName in new[] { "release_date", "upload_date" })
        {
            var value = GetString(root, propertyName);
            if (string.IsNullOrWhiteSpace(value))
                continue;
            foreach (var format in new[] { "yyyyMMdd", "yyyy-MM-dd", "yyyy" })
            {
                if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var parsed) && IsValidYear(parsed.Year))
                {
                    return (parsed.Year, format == "yyyy" ? value : parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }
            }
        }
        return (null, null);
    }

    private static bool TryGetYear(JsonElement root, string name, out int year)
    {
        year = 0;
        if (!root.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out year))
            return IsValidYear(year);
        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out year) &&
               IsValidYear(year);
    }

    private static bool IsValidYear(int year) => year is >= 1000 and <= 9999;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? LimitValue(property.GetString())
            : null;

    private static string? LimitValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= 512 ? value : value[..512];
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static string? FindCover(string stagingDirectory, long maxFileSize)
    {
        foreach (var name in new[] { "download.jpg", "download.jpeg" })
        {
            var path = Path.Combine(stagingDirectory, name);
            if (IsValidExistingFile(path, Math.Min(maxFileSize, 20 * 1024 * 1024)))
                return path;
        }
        return null;
    }

    private static string ResolveLibraryRoot(string configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
            throw new InvalidOperationException("Library root is missing.");
        if (!Path.IsPathFullyQualified(configuredRoot))
            throw new InvalidOperationException("Library root must be absolute.");
        return Path.GetFullPath(configuredRoot);
    }

    private static string ResolveExecutable(string? configuredPath, string bundledName, string fallbackName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!Path.IsPathFullyQualified(configuredPath))
                throw new InvalidOperationException("Configured media tool path must be absolute.");
            return Path.GetFullPath(configuredPath);
        }
        var bundledPath = Path.Combine(AppContext.BaseDirectory, bundledName);
        return File.Exists(bundledPath) ? bundledPath : fallbackName;
    }

    private static bool IsValidExistingFile(string path, long maximumBytes)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0 && info.Length <= maximumBytes;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static void DeleteIfEmptyBestEffort(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); } catch { }
    }

    private static YouTubeMusicActionResult SafeFailure() =>
        YouTubeMusicActionResult.Failed("Couldn't download that YouTube track right now.");

    private static YouTubeMusicActionResult AlreadyDownloaded(string artist, string title) =>
        YouTubeMusicActionResult.Succeeded(
            $"**{Limit(artist, 70)} — {Limit(title, 70)}** is already downloaded. " +
            "Plex/Plexamp can find it after the next library scan.");

    private void LogSafe(Exception exception, string category) =>
        _logger.LogWarning(
            "YouTube music action failed ({ExceptionType}, {FailureCategory}).",
            exception.GetType().Name,
            category);

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";

    private VideoLockState AcquireVideoLock(string videoId)
    {
        lock (_gateSync)
        {
            if (!_videoLocks.TryGetValue(videoId, out var state))
            {
                state = new VideoLockState();
                _videoLocks.Add(videoId, state);
            }
            state.ReferenceCount++;
            return state;
        }
    }

    private void ReleaseVideoLock(string videoId, VideoLockState state)
    {
        lock (_gateSync)
        {
            state.ReferenceCount--;
            if (state.ReferenceCount != 0)
                return;
            if (_videoLocks.TryGetValue(videoId, out var current) && ReferenceEquals(current, state))
                _videoLocks.Remove(videoId);
            state.Gate.Dispose();
        }
    }

    private sealed record CompletedDownload(string Path, string Artist, string Title);

    private sealed class VideoLockState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public CompletedDownload? Completed { get; set; }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex YouTubeVideoIdPattern();
}

public sealed record YouTubeTrackMetadata(
    string Title,
    string Artist,
    string Album,
    string Genre,
    int? Year,
    string? Date,
    double DurationSeconds);
