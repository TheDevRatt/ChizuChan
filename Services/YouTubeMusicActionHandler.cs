using System.Buffers.Binary;
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
            return await DownloadCoreAsync(canonicalVideoId, cancellationToken);
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
            EnsureLibraryRootSafe(root);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSafe(exception, "configuration");
            return YouTubeMusicActionResult.Failed("YouTube downloads are not configured correctly.");
        }

        string? stagingRoot = null;
        string? stagingDirectory = null;
        var stagingWasValidated = false;
        try
        {
            var locksRoot = Path.Combine(root, ".chizu-locks");
            CreateDirectorySafely(root, locksRoot);
            await using var processLock = await AcquireProcessLockAsync(root, locksRoot, videoId, cancellationToken);

            EnsureLibraryRootSafe(root);
            var indexRoot = Path.Combine(root, ".chizu-index");
            CreateDirectorySafely(root, indexRoot);
            var indexPath = Path.Combine(indexRoot, $"{videoId}.json");
            var indexStatus = TryResolveIndexedDownload(
                root, indexPath, videoId, _options.GetMaxFileSizeBytes(), out _);
            if (indexStatus == IndexReadStatus.Valid)
                return AlreadyDownloaded();
            if (indexStatus == IndexReadStatus.Invalid)
                QuarantineInvalidIndex(root, indexRoot, indexPath);

            stagingRoot = Path.Combine(root, ".chizu-staging");
            CreateDirectorySafely(root, stagingRoot);
            stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            CreateDirectorySafely(root, stagingDirectory);
            stagingWasValidated = true;

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
            EnsureSafeComponents(root, destination);
            if (File.Exists(destination))
            {
                if (!IsValidM4a(destination, _options.GetMaxFileSizeBytes()))
                    return YouTubeMusicActionResult.Failed("A conflicting library item already exists for that track.");
                await WriteIndexAtomicallyAsync(root, indexRoot, indexPath, videoId, destination, cancellationToken);
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
            if (!IsValidM4a(audioPath, maxFileSize))
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
            if (tag.ExitCode != 0 || !IsValidM4a(finalStagedPath, maxFileSize))
                return SafeFailure();

            var destinationDirectory = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("Destination has no parent directory.");
            CreateDirectorySafely(root, destinationDirectory);
            EnsureSafeComponents(root, destination);
            EnsureSafeComponents(root, stagingDirectory);
            if (File.Exists(destination))
            {
                if (!IsValidM4a(destination, maxFileSize))
                    return YouTubeMusicActionResult.Failed("A conflicting library item already exists for that track.");
                await WriteIndexAtomicallyAsync(root, indexRoot, indexPath, videoId, destination, cancellationToken);
                return AlreadyDownloaded(metadata.Artist, metadata.Title);
            }

            try
            {
                // Staging and destination are deliberately below the same configured root, so this
                // non-overwriting move is an atomic same-volume promotion.
                File.Move(finalStagedPath, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                EnsureSafeComponents(root, destination);
                if (!IsValidM4a(destination, maxFileSize))
                    return YouTubeMusicActionResult.Failed("A conflicting library item already exists for that track.");
                await WriteIndexAtomicallyAsync(root, indexRoot, indexPath, videoId, destination, cancellationToken);
                return AlreadyDownloaded(metadata.Artist, metadata.Title);
            }

            if (!IsValidM4a(destination, maxFileSize))
                return SafeFailure();
            await WriteIndexAtomicallyAsync(root, indexRoot, indexPath, videoId, destination, cancellationToken);
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
            if (stagingWasValidated && stagingRoot is not null && stagingDirectory is not null)
                DeleteStagingBestEffort(root, stagingRoot, stagingDirectory);
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
            if (!string.Equals(extractor, "youtube", StringComparison.Ordinal))
                return false;
            if (root.TryGetProperty("extractor_key", out var extractorKey) &&
                (extractorKey.ValueKind != JsonValueKind.String ||
                 !string.Equals(extractorKey.GetString(), "Youtube", StringComparison.Ordinal)))
            {
                return false;
            }
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
        if (!string.Equals(GetString(root, "live_status"), "not_live", StringComparison.Ordinal))
            return true;
        if (root.TryGetProperty("is_live", out var isLive) && isLive.ValueKind == JsonValueKind.True)
            return true;
        return root.TryGetProperty("was_live", out var wasLive) && wasLive.ValueKind == JsonValueKind.True;
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
            if (IsValidBoundedFile(path, Math.Min(maxFileSize, 20 * 1024 * 1024)))
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

    private static void EnsureLibraryRootSafe(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var attributes = File.GetAttributes(fullRoot);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The configured library root is not a safe directory.");
        }
    }

    private static void EnsureSafeComponents(string root, string candidate)
    {
        EnsureLibraryRootSafe(root);
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        if (!YouTubeMusicPathPolicy.IsWithinRoot(fullRoot, fullCandidate))
            throw new InvalidOperationException("A media path escaped the configured library root.");

        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative == ".")
            return;
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A media path escaped the configured library root.");
        }

        var current = fullRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A reparse point was found below the media root.");
        }
    }

    private static void CreateDirectorySafely(string root, string directory)
    {
        EnsureSafeComponents(root, directory);
        var fullRoot = Path.GetFullPath(root);
        var fullDirectory = Path.GetFullPath(directory);
        var relative = Path.GetRelativePath(fullRoot, fullDirectory);
        var current = fullRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) && !Directory.Exists(current))
                throw new InvalidOperationException("A required media directory is occupied by a file.");
            Directory.CreateDirectory(current);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("A media directory is not safe.");
            }
        }
        EnsureSafeComponents(root, fullDirectory);
        if (!YouTubeMusicPathPolicy.IsWithinRoot(fullRoot, fullDirectory))
            throw new InvalidOperationException("A media directory escaped the configured library root.");
    }

    private static async Task<FileStream> AcquireProcessLockAsync(
        string root,
        string locksRoot,
        string videoId,
        CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(locksRoot, $"{videoId}.lock");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeComponents(root, locksRoot);
            EnsureSafeComponents(root, lockPath);
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                try
                {
                    EnsureSafeComponents(root, lockPath);
                    if ((File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("The media lock file is not safe.");
                    return stream;
                }
                catch
                {
                    await stream.DisposeAsync();
                    throw;
                }
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }
    }

    private static IndexReadStatus TryResolveIndexedDownload(
        string root,
        string indexPath,
        string expectedVideoId,
        long maximumBytes,
        out string? destination)
    {
        destination = null;
        if (!File.Exists(indexPath))
            return IndexReadStatus.Missing;

        try
        {
            EnsureSafeComponents(root, indexPath);
            var info = new FileInfo(indexPath);
            if (!info.Exists || info.Length is <= 0 or > 16 * 1024)
                return IndexReadStatus.Invalid;
            var bytes = new byte[checked((int)info.Length)];
            using (var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length != bytes.Length)
                    return IndexReadStatus.Invalid;
                stream.ReadExactly(bytes);
                if (stream.Position != stream.Length)
                    return IndexReadStatus.Invalid;
            }

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 4,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            var element = document.RootElement;
            if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != 3 ||
                !element.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 ||
                !element.TryGetProperty("videoId", out var videoId) ||
                videoId.ValueKind != JsonValueKind.String ||
                !string.Equals(videoId.GetString(), expectedVideoId, StringComparison.Ordinal) ||
                !element.TryGetProperty("relativePath", out var pathElement) ||
                pathElement.ValueKind != JsonValueKind.String)
            {
                return IndexReadStatus.Invalid;
            }

            var relativePath = pathElement.GetString();
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 1024 ||
                Path.IsPathRooted(relativePath) || Path.IsPathFullyQualified(relativePath) ||
                relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => segment is "." or "..") ||
                !relativePath.EndsWith($"[{expectedVideoId}].m4a", StringComparison.Ordinal))
            {
                return IndexReadStatus.Invalid;
            }

            var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!YouTubeMusicPathPolicy.IsWithinRoot(root, candidate))
                return IndexReadStatus.Invalid;
            EnsureSafeComponents(root, candidate);
            if (!IsValidM4a(candidate, maximumBytes))
                return IndexReadStatus.Invalid;
            destination = candidate;
            return IndexReadStatus.Valid;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                           InvalidOperationException or ArgumentException or OverflowException)
        {
            return IndexReadStatus.Invalid;
        }
    }

    private static void QuarantineInvalidIndex(string root, string indexRoot, string indexPath)
    {
        if (!File.Exists(indexPath))
            return;
        EnsureSafeComponents(root, indexRoot);
        EnsureSafeComponents(root, indexPath);
        var quarantinePath = Path.Combine(
            indexRoot,
            $"{Path.GetFileName(indexPath)}.invalid-{Guid.NewGuid():N}");
        EnsureSafeComponents(root, quarantinePath);
        File.Move(indexPath, quarantinePath, overwrite: false);
    }

    private static async Task WriteIndexAtomicallyAsync(
        string root,
        string indexRoot,
        string indexPath,
        string videoId,
        string destination,
        CancellationToken cancellationToken)
    {
        EnsureSafeComponents(root, destination);
        if (!IsValidM4a(destination, long.MaxValue))
            throw new InvalidOperationException("The promoted media file is invalid.");
        CreateDirectorySafely(root, indexRoot);
        EnsureSafeComponents(root, indexPath);

        var relativePath = Path.GetRelativePath(root, destination);
        if (Path.IsPathRooted(relativePath) || !YouTubeMusicPathPolicy.IsWithinRoot(root, destination))
            throw new InvalidOperationException("The indexed media path is not root-relative.");

        byte[] json;
        using (var memory = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(memory))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WriteString("videoId", videoId);
                writer.WriteString("relativePath", relativePath);
                writer.WriteEndObject();
            }
            json = memory.ToArray();
        }
        if (json.Length > 16 * 1024)
            throw new InvalidOperationException("The media index record is too large.");

        var temporaryPath = Path.Combine(indexRoot, $".{videoId}.{Guid.NewGuid():N}.tmp");
        EnsureSafeComponents(root, temporaryPath);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            EnsureLibraryRootSafe(root);
            EnsureSafeComponents(root, destination);
            EnsureSafeComponents(root, indexRoot);
            EnsureSafeComponents(root, indexPath);
            EnsureSafeComponents(root, temporaryPath);
            File.Move(temporaryPath, indexPath, overwrite: true);
        }
        finally
        {
            try
            {
                EnsureSafeComponents(root, temporaryPath);
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch { }
        }
    }

    private static bool IsValidBoundedFile(string path, long maximumBytes)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0 && info.Length <= maximumBytes &&
                   (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidM4a(string path, long maximumBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 16 || info.Length > maximumBytes ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !string.Equals(info.Extension, ".m4a", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var length = (int)Math.Min(info.Length, 4096);
            Span<byte> header = length <= 512 ? stackalloc byte[length] : new byte[length];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            stream.ReadExactly(header);
            var boxSize = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (boxSize < 16 || boxSize > info.Length ||
                !header.Slice(4, 4).SequenceEqual("ftyp"u8))
            {
                return false;
            }

            var brandBytes = Math.Min((int)boxSize, header.Length);
            for (var offset = 8; offset + 4 <= brandBytes; offset += offset == 8 ? 8 : 4)
            {
                var brand = header.Slice(offset, 4);
                if (brand.SequenceEqual("M4A "u8) || brand.SequenceEqual("M4B "u8) ||
                    brand.SequenceEqual("isom"u8) || brand.SequenceEqual("mp41"u8) ||
                    brand.SequenceEqual("mp42"u8))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteStagingBestEffort(string root, string stagingRoot, string stagingDirectory)
    {
        try
        {
            EnsureLibraryRootSafe(root);
            EnsureSafeComponents(root, stagingRoot);
            EnsureSafeComponents(root, stagingDirectory);
            if (!YouTubeMusicPathPolicy.IsWithinRoot(stagingRoot, stagingDirectory))
                return;
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
            EnsureSafeComponents(root, stagingRoot);
            if (Directory.Exists(stagingRoot) && !Directory.EnumerateFileSystemEntries(stagingRoot).Any())
                Directory.Delete(stagingRoot);
        }
        catch { }
    }

    private static YouTubeMusicActionResult SafeFailure() =>
        YouTubeMusicActionResult.Failed("Couldn't download that YouTube track right now.");

    private static YouTubeMusicActionResult AlreadyDownloaded() =>
        YouTubeMusicActionResult.Succeeded(
            "That YouTube track is already downloaded. " +
            "Plex/Plexamp can find it after the next library scan.");

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

    private enum IndexReadStatus
    {
        Missing,
        Invalid,
        Valid,
    }

    private sealed class VideoLockState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
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
