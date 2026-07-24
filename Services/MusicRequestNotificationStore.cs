using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public sealed class MusicRequestNotificationStore : IMusicRequestNotificationStore
{
    private const int CurrentVersion = 2;
    private const int VersionOne = 1;
    private const int MaximumTextLength = 500;
    private readonly MusicRequestNotificationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _storeLock;
    private List<MusicRequestNotificationDTO> _records = [];
    private bool _initialized;
    private int _disposeState;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public MusicRequestNotificationStore(
        IOptions<MusicRequestNotificationOptions> options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
        ValidateOptions(_options);
        _storeLock = AcquireStoreLock(_options.StorePath);
    }

    private static FileStream AcquireStoreLock(string storePath)
    {
        var primaryPath = Path.GetFullPath(storePath);
        var directory = Path.GetDirectoryName(primaryPath)
            ?? throw new InvalidOperationException("The notification store path must have a directory.");
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(
                primaryPath + ".lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another process or store instance already owns the music notification store.",
                exception);
        }
    }

    public Task<MusicRequestNotificationDTO> AddAsync(
        MusicRequestNotificationDTO notification,
        CancellationToken cancellationToken = default) =>
        AddOrGetAsync(notification, cancellationToken);

    public async Task<MusicRequestNotificationDTO> AddOrGetAsync(
        MusicRequestNotificationDTO notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var existing = _records.FirstOrDefault(record =>
                IsActive(record.State) &&
                record.LidarrAlbumId == notification.LidarrAlbumId &&
                record.DiscordUserId == notification.DiscordUserId &&
                record.DmChannelId == notification.DmChannelId);
            if (existing is not null)
                return Clone(existing);

            var normalized = NormalizeNew(notification);
            var proposed = Prune(_records);
            MakeRoomForOne(proposed);
            proposed.Add(normalized);
            await PersistAsync(proposed, cancellationToken);
            _records = proposed;
            return Clone(normalized);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MusicRequestNotificationDTO?> GetAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await PersistPruningIfNeededAsync(cancellationToken);
            var record = _records.FirstOrDefault(item => item.RequestId == requestId);
            return record is null ? null : Clone(record);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MusicRequestNotificationDTO>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await PersistPruningIfNeededAsync(cancellationToken);
            return _records.Select(Clone).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MusicRequestNotificationDTO>> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await GetAllAsync(cancellationToken);
        return records.Where(record => IsActive(record.State)).ToArray();
    }

    public Task<MusicRequestNotificationDTO> MarkCompletionObservedAsync(
        Guid requestId,
        int? completionHistoryId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (completionHistoryId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(completionHistoryId));
        if (observedAtUtc == default)
            throw new ArgumentOutOfRangeException(nameof(observedAtUtc));

        return TransitionAsync(requestId, record =>
        {
            if (record.State is not (MusicRequestNotificationState.Pending or
                MusicRequestNotificationState.CompletionObserved))
                throw new InvalidOperationException("Only an active notification can observe completion.");

            return record with
            {
                State = MusicRequestNotificationState.CompletionObserved,
                CompletionHistoryId = completionHistoryId,
                CompletionObservedAtUtc = observedAtUtc.ToUniversalTime(),
                NextAttemptAtUtc = null,
            };
        }, cancellationToken);
    }

    public Task<MusicRequestNotificationDTO> SchedulePendingRecheckAsync(
        Guid requestId,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (nextAttemptAtUtc == default)
            throw new ArgumentOutOfRangeException(nameof(nextAttemptAtUtc));

        return TransitionAsync(requestId, record =>
        {
            if (record.State != MusicRequestNotificationState.Pending)
                throw new InvalidOperationException("Only a pending notification can schedule a completion recheck.");

            return record with
            {
                NextAttemptAtUtc = nextAttemptAtUtc.ToUniversalTime(),
                LastErrorCategory = null,
            };
        }, cancellationToken);
    }

    public Task<MusicRequestNotificationDTO> RecordAttemptFailureAsync(
        Guid requestId,
        string errorCategory,
        DateTimeOffset? nextAttemptAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(errorCategory, nameof(errorCategory), 100);
        return TransitionAsync(requestId, record =>
        {
            if (!IsActive(record.State))
                throw new InvalidOperationException("Only an active notification can record an attempt.");

            return record with
            {
                AttemptCount = checked(record.AttemptCount + 1),
                NextAttemptAtUtc = nextAttemptAtUtc?.ToUniversalTime(),
                LastErrorCategory = errorCategory.Trim(),
            };
        }, cancellationToken);
    }

    public Task<MusicRequestNotificationDTO> MarkNotifiedAsync(
        Guid requestId,
        ulong notificationMessageId,
        CancellationToken cancellationToken = default)
    {
        if (notificationMessageId == 0)
            throw new ArgumentOutOfRangeException(nameof(notificationMessageId));

        return TransitionAsync(requestId, record =>
        {
            if (record.State != MusicRequestNotificationState.CompletionObserved)
                throw new InvalidOperationException("Completion must be observed before notification.");

            return record with
            {
                State = MusicRequestNotificationState.Notified,
                NotificationMessageId = notificationMessageId,
                NextAttemptAtUtc = null,
                LastErrorCategory = null,
            };
        }, cancellationToken);
    }

    public Task<MusicRequestNotificationDTO> MarkDeadLetterAsync(
        Guid requestId,
        string errorCategory,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(errorCategory, nameof(errorCategory), 100);
        return TransitionAsync(requestId, record =>
        {
            if (!IsActive(record.State))
                throw new InvalidOperationException("Only an active notification can be dead-lettered.");

            return record with
            {
                State = MusicRequestNotificationState.DeadLetter,
                LastErrorCategory = errorCategory.Trim(),
                NextAttemptAtUtc = null,
            };
        }, cancellationToken);
    }

    private async Task<MusicRequestNotificationDTO> TransitionAsync(
        Guid requestId,
        Func<MusicRequestNotificationDTO, MusicRequestNotificationDTO> transition,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
            throw new ArgumentException("A request ID is required.", nameof(requestId));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var index = _records.FindIndex(record => record.RequestId == requestId);
            if (index < 0)
                throw new KeyNotFoundException("The music notification request was not found.");

            var changed = transition(_records[index]);
            ValidateStoredRecord(changed);
            var proposed = _records.ToList();
            proposed[index] = changed;
            proposed = Prune(proposed);
            await PersistAsync(proposed, cancellationToken);
            _records = proposed;
            return Clone(changed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        var primaryPath = Path.GetFullPath(_options.StorePath);
        var backupPath = primaryPath + ".bak";
        StoreDocument? document = null;
        var primaryRequiresMigration = false;
        Exception? primaryError = null;

        if (File.Exists(primaryPath))
        {
            try
            {
                var read = await ReadDocumentAsync(primaryPath, cancellationToken);
                document = read.Document;
                primaryRequiresMigration = read.RequiresMigration;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                primaryError = ex;
            }
        }

        if (document is not null && primaryRequiresMigration)
            await PersistAsync(document.Records, cancellationToken);

        if (document is null && File.Exists(backupPath))
        {
            try
            {
                var read = await ReadDocumentAsync(backupPath, cancellationToken);
                document = read.Document;
                await RestorePrimaryFromBackupAsync(document, primaryPath, cancellationToken);
            }
            catch (Exception backupError) when (
                backupError is JsonException or InvalidDataException or IOException)
            {
                if (primaryError is not null)
                {
                    throw new InvalidDataException(
                        "The music notification store and its backup are invalid.",
                        new AggregateException(primaryError, backupError));
                }

                throw new InvalidDataException("The music notification backup is invalid.", backupError);
            }
        }

        if (document is null && primaryError is not null)
            throw new InvalidDataException("The music notification store is invalid and no backup is available.", primaryError);

        var loaded = document?.Records.Select(Clone).ToList() ?? [];
        var pruned = Prune(loaded);
        EnforceMaximum(pruned);

        if (document is not null && pruned.Count != loaded.Count)
            await PersistAsync(pruned, cancellationToken);

        _records = pruned;
        _initialized = true;
    }

    private async Task<DocumentReadResult> ReadDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > _options.MaxFileSizeBytes)
            throw new InvalidDataException("The music notification store exceeds its size limit.");

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<StoreDocument>(
            stream, JsonOptions, cancellationToken);
        if (document is null || document.Version is not (VersionOne or CurrentVersion) || document.Records is null)
            throw new InvalidDataException("The music notification store has an unsupported format.");

        if (document.Records.Count > _options.MaxRecords)
            throw new InvalidDataException("The music notification store contains too many records.");

        var requiresMigration = document.Version == VersionOne;
        var records = requiresMigration
            ? document.Records.Select(MigrateVersionOneRecord).ToList()
            : document.Records;
        var requestIds = new HashSet<Guid>();
        foreach (var record in records)
        {
            if (record is null)
                throw new InvalidDataException("The music notification store contains a null record.");
            ValidateStoredRecord(record);
            if (!requestIds.Add(record.RequestId))
                throw new InvalidDataException("The music notification store contains duplicate request IDs.");
        }

        return new DocumentReadResult(
            new StoreDocument { Version = CurrentVersion, Records = records },
            requiresMigration);
    }

    private static MusicRequestNotificationDTO MigrateVersionOneRecord(
        MusicRequestNotificationDTO? record)
    {
        if (record is null)
            throw new InvalidDataException("The music notification store contains a null record.");

        var nonce = record.NotificationNonce;
        if (nonce is null || nonce.Length <= 25)
            return record;
        if (nonce.Length != 32 || nonce.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f') and
                not (>= 'A' and <= 'F')))
        {
            throw new InvalidDataException(
                "A version-one notification nonce is not a GUID-N hexadecimal value.");
        }

        return record with
        {
            NotificationNonce = nonce[..24].ToLowerInvariant(),
        };
    }

    private async Task PersistPruningIfNeededAsync(CancellationToken cancellationToken)
    {
        var proposed = Prune(_records);
        if (proposed.Count == _records.Count)
            return;

        await PersistAsync(proposed, cancellationToken);
        _records = proposed;
    }

    private async Task PersistAsync(
        IReadOnlyList<MusicRequestNotificationDTO> records,
        CancellationToken cancellationToken)
    {
        EnforceMaximum(records);
        var documentBytes = await SerializeDocumentAsync(records, cancellationToken);
        var primaryPath = Path.GetFullPath(_options.StorePath);
        var directory = Path.GetDirectoryName(primaryPath)
            ?? throw new InvalidOperationException("The notification store path must have a directory.");
        Directory.CreateDirectory(directory);
        var backupPath = primaryPath + ".bak";
        var tempPath = Path.Combine(
            directory, $".{Path.GetFileName(primaryPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await WriteDocumentAsync(tempPath, documentBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(primaryPath))
            {
                try
                {
                    File.Replace(tempPath, primaryPath, backupPath, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    ReplaceWithFallback(tempPath, primaryPath, backupPath);
                }
                catch (IOException)
                {
                    ReplaceWithFallback(tempPath, primaryPath, backupPath);
                }
            }
            else
            {
                File.Move(tempPath, primaryPath);
                File.Copy(primaryPath, backupPath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private async Task RestorePrimaryFromBackupAsync(
        StoreDocument document,
        string primaryPath,
        CancellationToken cancellationToken)
    {
        var documentBytes = await SerializeDocumentAsync(document.Records, cancellationToken);
        var directory = Path.GetDirectoryName(primaryPath)
            ?? throw new InvalidOperationException("The notification store path must have a directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory, $".{Path.GetFileName(primaryPath)}.{Guid.NewGuid():N}.recovery.tmp");
        try
        {
            await WriteDocumentAsync(tempPath, documentBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, primaryPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void ReplaceWithFallback(string tempPath, string primaryPath, string backupPath)
    {
        File.Copy(primaryPath, backupPath, overwrite: true);
        File.Move(tempPath, primaryPath, overwrite: true);
    }

    private async Task<byte[]> SerializeDocumentAsync(
        IReadOnlyList<MusicRequestNotificationDTO> records,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(
            stream,
            new StoreDocument { Version = CurrentVersion, Records = records.ToList() },
            JsonOptions,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Length > _options.MaxFileSizeBytes)
            throw new InvalidDataException("The music notification store exceeds its size limit.");

        return stream.ToArray();
    }

    private static async Task WriteDocumentAsync(
        string path,
        ReadOnlyMemory<byte> documentBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(documentBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private MusicRequestNotificationDTO NormalizeNew(MusicRequestNotificationDTO notification)
    {
        if (notification.State != MusicRequestNotificationState.Pending)
            throw new ArgumentException("A new notification must be pending.", nameof(notification));

        var normalized = notification with
        {
            RequestId = Guid.NewGuid(),
            ForeignAlbumId = notification.ForeignAlbumId.Trim(),
            ArtistName = notification.ArtistName.Trim(),
            AlbumTitle = notification.AlbumTitle.Trim(),
            RequestedAtUtc = notification.RequestedAtUtc == default
                ? _timeProvider.GetUtcNow()
                : notification.RequestedAtUtc.ToUniversalTime(),
            NotificationNonce = string.IsNullOrWhiteSpace(notification.NotificationNonce)
                ? GenerateNotificationNonce()
                : notification.NotificationNonce.Trim(),
            CompletionHistoryId = null,
            CompletionObservedAtUtc = null,
            NotificationMessageId = null,
            AttemptCount = 0,
            NextAttemptAtUtc = null,
            LastErrorCategory = null,
        };
        ValidateStoredRecord(normalized);
        return normalized;
    }

    private static string GenerateNotificationNonce()
    {
        // Discord accepts at most 25 nonce characters. Eighteen random bytes encode to exactly
        // 24 unpadded Base64URL characters while retaining 144 bits of cryptographic entropy.
        Span<byte> random = stackalloc byte[18];
        RandomNumberGenerator.Fill(random);
        return Convert.ToBase64String(random)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private List<MusicRequestNotificationDTO> Prune(IEnumerable<MusicRequestNotificationDTO> source)
    {
        var cutoff = _timeProvider.GetUtcNow().AddDays(-_options.TerminalRetentionDays);
        return source.Where(record =>
            IsActive(record.State) ||
            (record.CompletionObservedAtUtc ?? record.RequestedAtUtc) >= cutoff).ToList();
    }

    private void MakeRoomForOne(List<MusicRequestNotificationDTO> records)
    {
        while (records.Count >= _options.MaxRecords)
        {
            var removable = records
                .Select((record, index) => (record, index))
                .Where(item => !IsActive(item.record.State))
                .OrderBy(item => item.record.CompletionObservedAtUtc ?? item.record.RequestedAtUtc)
                .FirstOrDefault();
            if (removable.record is null)
                throw new InvalidOperationException("The music notification store is at capacity.");
            records.RemoveAt(removable.index);
        }
    }

    private void EnforceMaximum(IReadOnlyCollection<MusicRequestNotificationDTO> records)
    {
        if (records.Count > _options.MaxRecords)
            throw new InvalidDataException("The music notification store exceeds its record limit.");
    }

    private static void ValidateOptions(MusicRequestNotificationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.StorePath))
            throw new ArgumentException("A notification store path is required.", nameof(options));
        if (options.MaxRecords <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRecords must be positive.");
        if (options.TerminalRetentionDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "TerminalRetentionDays must be positive.");
        if (options.MaxFileSizeBytes < 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxFileSizeBytes is too small.");
    }

    private static void ValidateStoredRecord(MusicRequestNotificationDTO record)
    {
        if (record.RequestId == Guid.Empty)
            throw new InvalidDataException("A notification request ID is required.");
        if (record.LidarrAlbumId <= 0)
            throw new InvalidDataException("A positive Lidarr album ID is required.");
        if (record.DiscordUserId == 0 || record.DmChannelId == 0)
            throw new InvalidDataException("Discord user and channel IDs are required.");
        ValidateStoredText(record.ForeignAlbumId, nameof(record.ForeignAlbumId), 200);
        ValidateStoredText(record.ArtistName, nameof(record.ArtistName), MaximumTextLength);
        ValidateStoredText(record.AlbumTitle, nameof(record.AlbumTitle), MaximumTextLength);
        ValidateStoredText(record.NotificationNonce, nameof(record.NotificationNonce), 25);
        if (record.RequestedAtUtc == default)
            throw new InvalidDataException("A request timestamp is required.");
        if (!Enum.IsDefined(record.State))
            throw new InvalidDataException("The notification state is invalid.");
        if (record.CompletionHistoryId is <= 0)
            throw new InvalidDataException("The completion history ID must be positive.");
        if (record.CompletionObservedAtUtc == default && record.CompletionObservedAtUtc is not null)
            throw new InvalidDataException("The completion timestamp is invalid.");
        if (record.NotificationMessageId == 0)
            throw new InvalidDataException("The notification message ID must be positive.");
        if (record.NextAttemptAtUtc == default && record.NextAttemptAtUtc is not null)
            throw new InvalidDataException("The next-attempt timestamp is invalid.");
        if (record.AttemptCount < 0)
            throw new InvalidDataException("The attempt count cannot be negative.");
        if (record.LastErrorCategory is not null)
            ValidateStoredText(record.LastErrorCategory, nameof(record.LastErrorCategory), 100);

        var hasCompletionTimestamp = record.CompletionObservedAtUtc.HasValue;
        if ((record.State is MusicRequestNotificationState.CompletionObserved or MusicRequestNotificationState.Notified) &&
            !hasCompletionTimestamp)
            throw new InvalidDataException("The notification state requires a completion timestamp.");
        if (record.State == MusicRequestNotificationState.Pending &&
            (record.CompletionHistoryId.HasValue || hasCompletionTimestamp))
            throw new InvalidDataException("A pending notification cannot contain completion details.");
        if (record.State == MusicRequestNotificationState.Notified && !record.NotificationMessageId.HasValue)
            throw new InvalidDataException("A notified record requires a Discord message ID.");
        if (record.State != MusicRequestNotificationState.Notified && record.NotificationMessageId.HasValue)
            throw new InvalidDataException("Only a notified record can contain a Discord message ID.");
        if (!IsActive(record.State) && record.NextAttemptAtUtc.HasValue)
            throw new InvalidDataException("A terminal notification cannot have another attempt scheduled.");
    }

    private static void ValidateRequiredText(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new ArgumentException($"{parameterName} must be nonblank and no longer than {maxLength} characters.", parameterName);
    }

    private static void ValidateStoredText(string value, string fieldName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new InvalidDataException($"The stored {fieldName} is invalid.");
    }

    private static bool IsActive(MusicRequestNotificationState state) =>
        state is MusicRequestNotificationState.Pending or MusicRequestNotificationState.CompletionObserved;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _storeLock?.Dispose();
        _storeLock = null;
        _gate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        if (_storeLock is not null)
        {
            await _storeLock.DisposeAsync();
            _storeLock = null;
        }
        _gate.Dispose();
    }

    private static MusicRequestNotificationDTO Clone(MusicRequestNotificationDTO source) => source with { };

    private readonly record struct DocumentReadResult(
        StoreDocument Document,
        bool RequiresMigration);

    private sealed class StoreDocument
    {
        public int Version { get; set; }
        public List<MusicRequestNotificationDTO> Records { get; set; } = [];
    }
}
