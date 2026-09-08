using System.Buffers.Binary;
using System.Globalization;
using System.Text;
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
    private const string MetadataProjection =
        "%(.{_type,extractor,extractor_key,id,live_status,is_live,was_live,duration,track,title,artist,uploader,channel,album,genre,release_year,release_date,upload_date})j";
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
        if (!_options.RequireExclusiveLibraryRoot)
            return YouTubeMusicActionResult.Failed("YouTube downloads are not configured correctly.");

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

        string? locksRoot = null;
        string? stagingRoot = null;
        string? stagingDirectory = null;
        FileStream? rootOperationLock = null;
        FileStream? processLock = null;
        var stagingWasValidated = false;
        try
        {
            locksRoot = Path.Combine(root, ".chizu-locks");
            CreateDirectorySafely(root, locksRoot);
            EnsureLibraryRootSafe(root);
            EnsureSafeComponents(root, locksRoot);
            rootOperationLock = await AcquireFileLockAsync(
                root,
                locksRoot,
                "root-operation.lock",
                TimeSpan.FromSeconds(_options.GetRootLockTimeoutSeconds()),
                rootOperation: true,
                cancellationToken);

            // The ACL-owned root is revalidated after obtaining the global lock. The lock is held
            // through staging cleanup so every cooperating Chizu instance is a single writer.
            EnsureLibraryRootSafe(root);
            EnsureSafeComponents(root, locksRoot);
            processLock = await AcquireFileLockAsync(
                root,
                locksRoot,
                $"{videoId}.lock",
                TimeSpan.FromSeconds(_options.GetRootLockTimeoutSeconds()),
                rootOperation: false,
                cancellationToken);

            EnsureLibraryRootSafe(root);
            var indexRoot = Path.Combine(root, ".chizu-index");
            CreateDirectorySafely(root, indexRoot);
            var indexPath = Path.Combine(indexRoot, $"{videoId}.json");
            var indexStatus = TryResolveIndexedDownload(
                root, indexPath, videoId, 0, out var indexedDestination);
            if (indexStatus == IndexReadStatus.Valid && indexedDestination is not null &&
                await VerifyAudioStreamAsync(ffmpeg, indexedDestination, root, cancellationToken))
            {
                return AlreadyDownloaded();
            }
            if (indexStatus != IndexReadStatus.Missing)
            {
                QuarantineInvalidIndex(root, indexRoot, indexPath);
                if (indexedDestination is not null && File.Exists(indexedDestination))
                    QuarantineInvalidMedia(root, indexedDestination);
            }

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
                "--print",
                MetadataProjection,
                "--no-warnings",
                canonicalUrl,
            };
            var probe = await _tool.RunAsync(ApplyResourcePolicy(new YouTubeDownloadToolInvocation(
                ytDlp,
                probeArguments,
                stagingDirectory,
                timeout,
                metadataLimit,
                MaximumToolErrorCharacters), metadata: true), cancellationToken);
            if (probe.ExitCode != 0)
                return SafeFailure();

            if (!TryParseMetadata(probe.StandardOutput, videoId, _options, out var metadata, out var rejection))
                return YouTubeMusicActionResult.Failed(rejection);

            var destination = YouTubeMusicPathPolicy.BuildDestinationPath(
                root, metadata.Artist, metadata.Album, metadata.Title, videoId);
            EnsureSafeComponents(root, destination);
            if (File.Exists(destination))
            {
                if (await VerifyAudioStreamAsync(ffmpeg, destination, stagingDirectory, cancellationToken))
                {
                    await WriteIndexAtomicallyAsync(root, indexRoot, indexPath, videoId, destination, cancellationToken);
                    return AlreadyDownloaded(metadata.Artist, metadata.Title);
                }
                QuarantineInvalidMedia(root, destination);
            }

            var maxFileSize = _options.GetMaxFileSizeBytes();
            var downloadArguments = BuildDownloadArguments(
                canonicalUrl,
                stagingDirectory,
                ffmpeg,
                _options.GetMaxDurationSeconds(),
                maxFileSize);
            var download = await _tool.RunAsync(ApplyResourcePolicy(new YouTubeDownloadToolInvocation(
                ytDlp,
                downloadArguments,
                stagingDirectory,
                timeout,
                64 * 1024,
                MaximumToolErrorCharacters)), cancellationToken);
            if (download.ExitCode != 0)
                return SafeFailure();

            var audioPath = Path.Combine(stagingDirectory, "download.m4a");
            if (ExceedsFileSizePolicy(audioPath, maxFileSize))
                return YouTubeMusicActionResult.Failed("The download exceeds the operator-configured file size limit.");
            if (!IsValidM4a(audioPath, maxFileSize))
                return SafeFailure();

            var coverPath = FindCover(stagingDirectory, maxFileSize);
            var finalStagedPath = Path.Combine(stagingDirectory, $"final-{Guid.NewGuid():N}.m4a");
            var ffmpegArguments = BuildFfmpegArguments(
                audioPath, coverPath, finalStagedPath, metadata, canonicalUrl);
            var tag = await _tool.RunAsync(ApplyResourcePolicy(new YouTubeDownloadToolInvocation(
                ffmpeg,
                ffmpegArguments,
                stagingDirectory,
                timeout,
                64 * 1024,
                MaximumToolErrorCharacters)), cancellationToken);
            if (ExceedsFileSizePolicy(finalStagedPath, maxFileSize))
                return YouTubeMusicActionResult.Failed("The tagged download exceeds the operator-configured file size limit.");
            if (tag.ExitCode != 0 || !IsValidM4a(finalStagedPath, maxFileSize))
                return SafeFailure();
            if (!await VerifyAudioStreamAsync(ffmpeg, finalStagedPath, stagingDirectory, cancellationToken, metadata.DurationSeconds))
                return YouTubeMusicActionResult.Failed("Downloaded audio failed duration or tail verification; no file was imported.");

            cancellationToken.ThrowIfCancellationRequested();
            var destinationDirectory = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("Destination has no parent directory.");
            CreateDirectorySafely(root, destinationDirectory);
            EnsureSafeComponents(root, destination);
            EnsureSafeComponents(root, stagingDirectory);
            if (File.Exists(destination))
            {
                if (await VerifyAudioStreamAsync(ffmpeg, destination, stagingDirectory, cancellationToken))
                {
                    await WriteIndexAtomicallyAsync(root, indexRoot, indexPath, videoId, destination, cancellationToken);
                    return AlreadyDownloaded(metadata.Artist, metadata.Title);
                }
                QuarantineInvalidMedia(root, destination);
            }

            EnsureLibraryRootSafe(root);
            EnsureSafeComponents(root, locksRoot);
            EnsureSafeComponents(root, Path.Combine(locksRoot, "root-operation.lock"));
            EnsureSafeComponents(root, destination);
            EnsureSafeComponents(root, finalStagedPath);
            try
            {
                // Staging and destination are deliberately below the same configured root, so this
                // non-overwriting move is an atomic same-volume promotion.
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(finalStagedPath, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                EnsureSafeComponents(root, destination);
                if (!await VerifyAudioStreamAsync(ffmpeg, destination, stagingDirectory, cancellationToken))
                    return SafeFailure();
                await WriteIndexAtomicallyAsync(root, indexRoot, indexPath, videoId, destination, cancellationToken);
                return AlreadyDownloaded(metadata.Artist, metadata.Title);
            }

            if (!IsValidM4a(destination, maxFileSize))
                return SafeFailure();
            await WriteIndexAtomicallyAsync(root, indexRoot, indexPath, videoId, destination, cancellationToken);
            return YouTubeMusicActionResult.Succeeded(
                $"Downloaded **{EscapeDiscordText(metadata.Artist, 70)} — {EscapeDiscordText(metadata.Title, 70)}**. " +
                "Plex/Plexamp will pick it up on the next library scan.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (YouTubeLongMediaLowSpaceException)
        {
            return YouTubeMusicActionResult.Failed("The YouTube library has insufficient free space for its configured reserve. No download was imported.");
        }
        catch (YouTubeLongMediaStalledException)
        {
            return YouTubeMusicActionResult.Failed("The YouTube download stalled without progress and was stopped. Please try again.");
        }
        catch (TimeoutException)
        {
            return YouTubeMusicActionResult.Failed("The YouTube download exceeded the operator-configured elapsed time limit.");
        }
        catch (RootOperationLockUnavailableException)
        {
            return YouTubeMusicActionResult.Failed("The YouTube music library is busy. Please try again shortly.");
        }
        catch (Exception exception)
        {
            LogSafe(exception, "download");
            return SafeFailure();
        }
        finally
        {
            if (stagingWasValidated && stagingRoot is not null && stagingDirectory is not null &&
                rootOperationLock is not null)
            {
                DeleteStagingBestEffort(root, stagingRoot, stagingDirectory);
            }
            if (processLock is not null)
            {
                try { await processLock.DisposeAsync(); } catch { }
            }
            if (rootOperationLock is not null)
            {
                try { await rootOperationLock.DisposeAsync(); } catch { }
            }
        }
    }

    private YouTubeDownloadToolInvocation ApplyResourcePolicy(
        YouTubeDownloadToolInvocation invocation, bool metadata = false, bool readOnly = false) => invocation with
    {
        OutputMode = metadata ? YouTubeDownloadOutputMode.Metadata : YouTubeDownloadOutputMode.Diagnostics,
        MinimumFreeSpaceBytes = readOnly ? 0 : Math.Max(0, _options.MinimumFreeSpaceBytes),
        StalledWorkTimeout = TimeSpan.FromSeconds(Math.Max(0, _options.StalledWorkTimeoutSeconds)),
        MonitoringInterval = TimeSpan.FromMilliseconds(_options.ResourceMonitoringIntervalMilliseconds > 0
            ? _options.ResourceMonitoringIntervalMilliseconds : 1000),
    };

    public static IReadOnlyList<string> BuildDownloadArguments(
        string canonicalUrl,
        string stagingDirectory,
        string ffmpegPath,
        int maxDurationSeconds,
        long maxFileSizeBytes)
    {
        var arguments = new List<string> { "--ignore-config", "--no-playlist" };
        // Recheck live eligibility at acquisition to handle status changes after the probe.
        // Separate match filters are OR-ed by yt-dlp. Clauses within each filter are AND-ed.
        foreach (var status in new[] { "not_live", "was_live" })
        {
            var filter = $"!is_live & live_status = {status} & duration > 0";
            if (maxDurationSeconds > 0)
                filter += $" & duration <= {maxDurationSeconds.ToString(CultureInfo.InvariantCulture)}";
            arguments.AddRange(["--match-filter", filter]);
        }
        if (maxFileSizeBytes > 0)
            arguments.AddRange(["--max-filesize", maxFileSizeBytes.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange([
        "--format", "bestaudio/best",
        "--extract-audio",
        "--audio-format", "m4a",
        "--audio-quality", "0",
        "--write-thumbnail",
        "--convert-thumbnails", "jpg",
        "--ffmpeg-location", ffmpegPath,
        "--output", Path.Combine(stagingDirectory, "download.%(ext)s"),
        canonicalUrl,
        ]);
        return arguments;
    }

    public static IReadOnlyList<string> BuildFfmpegArguments(
        string audioPath,
        string? coverPath,
        string outputPath,
        YouTubeTrackMetadata metadata,
        string canonicalUrl)
    {
        var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-progress", "pipe:1", "-nostats", "-n", "-i", audioPath };
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
        else
        {
            // Retain an existing attached image when no replacement thumbnail was acquired.
            arguments.AddRange(["-map", "0:v?", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
        }
        arguments.AddRange([
            "-c:a", "copy",
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

    public static IReadOnlyList<string> BuildAudioVerificationArguments(string mediaPath) =>
    [
        "-hide_banner",
        "-loglevel", "error",
        "-nostdin",
        "-xerror",
        "-i", mediaPath,
        "-map", "0:a:0",
        "-progress", "pipe:1",
        "-nostats",
        "-f", "null",
        OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
    ];

    private async Task<bool> VerifyAudioStreamAsync(
        string ffmpegPath,
        string mediaPath,
        string workingDirectory,
        CancellationToken cancellationToken,
        double? expectedDuration = null)
    {
        // Admission policies never invalidate an existing library item.
        if (!IsValidM4a(mediaPath, 0))
            return false;

        var result = await _tool.RunAsync(ApplyResourcePolicy(new YouTubeDownloadToolInvocation(
            ffmpegPath,
            BuildAudioVerificationArguments(mediaPath),
            workingDirectory,
            TimeSpan.FromSeconds(_options.GetDownloadTimeoutSeconds()),
            8 * 1024,
            8 * 1024), readOnly: true), cancellationToken);
        if (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.StandardError) ||
            !result.StandardOutput.Contains("progress=end", StringComparison.Ordinal)) return false;
        var lastTime = result.StandardOutput.Split('\n')
            .LastOrDefault(line => line.StartsWith("out_time_us=", StringComparison.Ordinal));
        if (lastTime is null || !long.TryParse(lastTime[12..].Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var microseconds) || microseconds <= 0) return false;
        var duration = microseconds / 1_000_000d;
        // Fixed mux/codec rounding tolerance, not a percentage that hides minutes on long tracks.
        return expectedDuration is null || Math.Abs(duration - expectedDuration.Value) <= 2;

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
            if (root.TryGetProperty("_type", out var typeProperty) &&
                typeProperty.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return false;
            var resultType = GetString(root, "_type");
            if (resultType is not null && !string.Equals(resultType, "video", StringComparison.OrdinalIgnoreCase))
                return false;
            // The probe is a canonical single-video URL with --no-playlist and a fixed compact
            // projection. Do not project the potentially unbounded entries collection; playlist
            // envelopes are rejected by their non-video _type before any download starts.
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
            if (!HasValidLiveMetadata(root))
                return false;
            if (IsLiveOrPremiere(root))
            {
                rejectionMessage = "Live streams and premieres can't be downloaded.";
                return false;
            }
            if (!TryGetDuration(root, out var duration))
                return false;
            if (options.GetMaxDurationSeconds() > 0 && duration > options.GetMaxDurationSeconds())
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

    private static bool HasValidLiveMetadata(JsonElement root)
    {
        if (GetString(root, "live_status") is null) return false;
        foreach (var name in new[] { "is_live", "was_live" })
            if (root.TryGetProperty(name, out var value) &&
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                return false;
        return true;
    }

    private static bool IsLiveOrPremiere(JsonElement root) =>
        GetString(root, "live_status") is not ("not_live" or "was_live") ||
        (root.TryGetProperty("is_live", out var isLive) && isLive.ValueKind == JsonValueKind.True);

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
            if (IsValidBoundedFile(path, maxFileSize > 0 ? Math.Min(maxFileSize, 20 * 1024 * 1024) : 20 * 1024 * 1024))
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

    private static async Task<FileStream> AcquireFileLockAsync(
        string root,
        string locksRoot,
        string lockFileName,
        TimeSpan timeout,
        bool rootOperation,
        CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(locksRoot, lockFileName);
        var deadline = DateTime.UtcNow.Add(timeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLibraryRootSafe(root);
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
                    EnsureLibraryRootSafe(root);
                    EnsureSafeComponents(root, locksRoot);
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
            catch (IOException exception)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    if (rootOperation)
                        throw new RootOperationLockUnavailableException(exception);
                    throw;
                }

                await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100),
                    cancellationToken);
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
            destination = candidate;
            if (!IsValidM4a(candidate, maximumBytes))
                return IndexReadStatus.Invalid;
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

    private static void QuarantineInvalidMedia(string root, string mediaPath)
    {
        if (!File.Exists(mediaPath))
            return;
        EnsureLibraryRootSafe(root);
        EnsureSafeComponents(root, mediaPath);
        if ((File.GetAttributes(mediaPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The invalid media file is not safe to quarantine.");

        var quarantineRoot = Path.Combine(root, ".chizu-quarantine");
        CreateDirectorySafely(root, quarantineRoot);
        var quarantinePath = Path.Combine(
            quarantineRoot,
            $"{Path.GetFileNameWithoutExtension(mediaPath)}.invalid-{Guid.NewGuid():N}.m4a");
        EnsureSafeComponents(root, quarantinePath);
        EnsureLibraryRootSafe(root);
        EnsureSafeComponents(root, mediaPath);
        EnsureSafeComponents(root, quarantineRoot);
        File.Move(mediaPath, quarantinePath, overwrite: false);
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

    private static bool ExceedsFileSizePolicy(string path, long maximumBytes) =>
        maximumBytes > 0 && File.Exists(path) && new FileInfo(path).Length > maximumBytes;

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
            if (!info.Exists || info.Length < 16 || (maximumBytes > 0 && info.Length > maximumBytes) ||
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
            $"**{EscapeDiscordText(artist, 70)} — {EscapeDiscordText(title, 70)}** is already downloaded. " +
            "Plex/Plexamp can find it after the next library scan.");

    private void LogSafe(Exception exception, string category) =>
        _logger.LogWarning(
            "YouTube music action failed ({ExceptionType}, {FailureCategory}).",
            exception.GetType().Name,
            category);

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";

    private static string EscapeDiscordText(string value, int maximumLength)
    {
        var builder = new StringBuilder(maximumLength * 2);
        foreach (var character in Limit(value.Trim(), maximumLength))
        {
            if (char.IsControl(character))
            {
                builder.Append(' ');
                continue;
            }
            if (character == '@')
            {
                builder.Append("@\u200B");
                continue;
            }
            if (character is '\\' or '`' or '*' or '_' or '~' or '|' or '[' or ']' or '(' or ')' or '<' or '>')
                builder.Append('\\');
            builder.Append(character);
        }
        return builder.ToString();
    }

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

    private sealed class RootOperationLockUnavailableException : IOException
    {
        public RootOperationLockUnavailableException(IOException innerException)
            : base("The root operation lock could not be acquired.", innerException)
        {
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
