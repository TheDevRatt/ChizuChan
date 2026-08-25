using System.Text.Json;
using System.Text.Json.Serialization;
using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public sealed class DirectMusicRequestStatusStore : IDirectMusicRequestStatusStore
{
    private const int CurrentVersion = 1;
    private const int MaximumTextLength = 500;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DirectMusicRequestStatusOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _storeLock;
    private List<DirectMusicRequestStatusDTO> _records = [];
    private bool _initialized;
    private int _disposeState;

    public DirectMusicRequestStatusStore(
        IOptions<DirectMusicRequestStatusOptions> options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
        ValidateOptions(_options);
        _storeLock = AcquireStoreLock(_options.StorePath);
    }

    public async Task<DirectMusicRequestStatusDTO> AddAsync(
        DirectMusicRequestStatusDTO request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var normalized = NormalizeNew(request);
            if (_records.Any(item => item.BatchId == normalized.BatchId))
                throw new InvalidOperationException("A direct music request with this batch ID already exists.");

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

    public async Task<IReadOnlyList<DirectMusicRequestStatusDTO>> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await GetAllAsync(cancellationToken);
        return records.Where(item => IsActive(item.State))
            .OrderBy(item => item.UpdatedAtUtc)
            .ThenBy(item => item.RequestId)
            .ToArray();
    }

    public async Task<IReadOnlyList<DirectMusicRequestStatusDTO>> GetRecentForUserAsync(
        ulong discordUserId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (discordUserId == 0)
            throw new ArgumentOutOfRangeException(nameof(discordUserId));
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var records = await GetAllAsync(cancellationToken);
        return records.Where(item => item.DiscordUserId == discordUserId)
            .OrderByDescending(item => IsActive(item.State))
            .ThenByDescending(item => item.UpdatedAtUtc)
            .ThenByDescending(item => item.RequestedAtUtc)
            .Take(Math.Min(limit, 100))
            .ToArray();
    }

    public async Task<DirectMusicRequestStatusDTO> UpdateAsync(
        DirectMusicRequestStatusDTO request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty)
            throw new ArgumentException("A request ID is required.", nameof(request));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var index = _records.FindIndex(item => item.RequestId == request.RequestId);
            if (index < 0)
                throw new KeyNotFoundException("The direct music request was not found.");

            var current = _records[index];
            ValidateImmutableFields(current, request);
            if (!IsAllowedTransition(current.State, request.State))
                throw new InvalidOperationException("The direct music request state cannot regress.");
            if (request.BytesTransferred < current.BytesTransferred)
                throw new InvalidOperationException("Transferred bytes cannot decrease.");

            var changed = request with
            {
                RequestedAtUtc = current.RequestedAtUtc,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                Username = request.Username.Trim(),
                RemoteFilename = request.RemoteFilename.Trim(),
                ArtistName = request.ArtistName.Trim(),
                TrackTitle = request.TrackTitle.Trim(),
                PlexMediaPath = TrimOptional(request.PlexMediaPath),
                FailureCategory = TrimOptional(request.FailureCategory),

            };
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

    private async Task<IReadOnlyList<DirectMusicRequestStatusDTO>> GetAllAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var proposed = Prune(_records);
            if (proposed.Count != _records.Count)
            {
                await PersistAsync(proposed, cancellationToken);
                _records = proposed;
            }
            return _records.Select(Clone).ToArray();
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
        Exception? primaryError = null;

        if (File.Exists(primaryPath))
        {
            try
            {
                document = await ReadDocumentAsync(primaryPath, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
            {
                primaryError = exception;
            }
        }

        if (document is null && File.Exists(backupPath))
        {
            try
            {
                document = await ReadDocumentAsync(backupPath, cancellationToken);
                await RestorePrimaryAsync(document, primaryPath, cancellationToken);
            }
            catch (Exception backupError) when (backupError is JsonException or InvalidDataException or IOException)
            {
                if (primaryError is not null)
                {
                    throw new InvalidDataException(
                        "The direct music status store and its backup are invalid.",
                        new AggregateException(primaryError, backupError));
                }
                throw new InvalidDataException("The direct music status backup is invalid.", backupError);
            }
        }

        if (document is null && primaryError is not null)
            throw new InvalidDataException("The direct music status store is invalid and no backup is available.", primaryError);

        var loaded = document?.Records.Select(Clone).ToList() ?? [];
        var pruned = Prune(loaded);
        EnforceMaximum(pruned);
        if (document is not null && pruned.Count != loaded.Count)
            await PersistAsync(pruned, cancellationToken);

        _records = pruned;
        _initialized = true;
    }

    private async Task<StoreDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > _options.MaxFileSizeBytes)
            throw new InvalidDataException("The direct music status store exceeds its size limit.");

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<StoreDocument>(stream, JsonOptions, cancellationToken);
        if (document is null || document.Version != CurrentVersion || document.Records is null)
            throw new InvalidDataException("The direct music status store has an unsupported format.");
        if (document.Records.Count > _options.MaxRecords)
            throw new InvalidDataException("The direct music status store contains too many records.");

        var requestIds = new HashSet<Guid>();
        var batchIds = new HashSet<Guid>();
        foreach (var record in document.Records)
        {
            if (record is null)
                throw new InvalidDataException("The direct music status store contains a null record.");
            ValidateStoredRecord(record);
            if (!requestIds.Add(record.RequestId) || !batchIds.Add(record.BatchId))
                throw new InvalidDataException("The direct music status store contains duplicate identities.");
        }
        return document;
    }

    private async Task PersistAsync(
        IReadOnlyList<DirectMusicRequestStatusDTO> records,
        CancellationToken cancellationToken)
    {
        EnforceMaximum(records);
        var bytes = await SerializeAsync(records, cancellationToken);
        var primaryPath = Path.GetFullPath(_options.StorePath);
        var directory = Path.GetDirectoryName(primaryPath)
            ?? throw new InvalidOperationException("The direct music status path must have a directory.");
        Directory.CreateDirectory(directory);
        var backupPath = primaryPath + ".bak";
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(primaryPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteAsync(tempPath, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(primaryPath))
            {
                try
                {
                    File.Replace(tempPath, primaryPath, backupPath, ignoreMetadataErrors: true);
                }
                catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
                {
                    File.Copy(primaryPath, backupPath, overwrite: true);
                    File.Move(tempPath, primaryPath, overwrite: true);
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

    private async Task RestorePrimaryAsync(
        StoreDocument document,
        string primaryPath,
        CancellationToken cancellationToken)
    {
        var bytes = await SerializeAsync(document.Records, cancellationToken);
        var directory = Path.GetDirectoryName(primaryPath)
            ?? throw new InvalidOperationException("The direct music status path must have a directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(primaryPath)}.{Guid.NewGuid():N}.recovery.tmp");
        try
        {
            await WriteAsync(tempPath, bytes, cancellationToken);
            File.Move(tempPath, primaryPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private async Task<byte[]> SerializeAsync(
        IReadOnlyList<DirectMusicRequestStatusDTO> records,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(
            stream,
            new StoreDocument { Version = CurrentVersion, Records = records.ToList() },
            JsonOptions,
            cancellationToken);
        if (stream.Length > _options.MaxFileSizeBytes)
            throw new InvalidDataException("The direct music status store exceeds its size limit.");
        return stream.ToArray();
    }

    private static async Task WriteAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private DirectMusicRequestStatusDTO NormalizeNew(DirectMusicRequestStatusDTO request)
    {
        if (request.State is not (DirectMusicRequestState.DispatchPending or DirectMusicRequestState.Queued))
            throw new ArgumentException("A new direct music request must be pending dispatch or queued.", nameof(request));
        var now = _timeProvider.GetUtcNow();
        var normalized = request with
        {
            RequestId = Guid.NewGuid(),
            TransferId = null,
            Username = request.Username.Trim(),
            RemoteFilename = request.RemoteFilename.Trim(),
            ArtistName = request.ArtistName.Trim(),
            TrackTitle = request.TrackTitle.Trim(),
            BytesTransferred = 0,
            RequestedAtUtc = request.RequestedAtUtc == default ? now : request.RequestedAtUtc.ToUniversalTime(),
            UpdatedAtUtc = now,
            PlexMediaPath = null,
            FailureCategory = null,

        };
        ValidateStoredRecord(normalized);
        return normalized;
    }

    private static void ValidateImmutableFields(
        DirectMusicRequestStatusDTO current,
        DirectMusicRequestStatusDTO changed)
    {
        if ((current.TransferId.HasValue && current.TransferId != changed.TransferId) ||
            current.BatchId != changed.BatchId ||
            current.SearchId != changed.SearchId ||
            current.DiscordUserId != changed.DiscordUserId ||
            current.DmChannelId != changed.DmChannelId ||
            current.Username != changed.Username ||
            current.RemoteFilename != changed.RemoteFilename ||
            current.ArtistName != changed.ArtistName ||
            current.TrackTitle != changed.TrackTitle ||
            current.ExpectedSize != changed.ExpectedSize ||
            current.ExpectedDuration != changed.ExpectedDuration)
        {
            throw new InvalidOperationException("Direct music request correlation fields are immutable.");
        }
    }

    private static bool IsAllowedTransition(DirectMusicRequestState current, DirectMusicRequestState next) =>
        current == next || current switch
        {
            DirectMusicRequestState.DispatchPending => next is DirectMusicRequestState.Queued or
                DirectMusicRequestState.Downloading or DirectMusicRequestState.DownloadedWaitingForPlex or
                DirectMusicRequestState.ReadyInPlexamp or DirectMusicRequestState.Failed,
            DirectMusicRequestState.Queued => next is DirectMusicRequestState.Downloading or
                DirectMusicRequestState.DownloadedWaitingForPlex or DirectMusicRequestState.ReadyInPlexamp or
                DirectMusicRequestState.Failed,
            DirectMusicRequestState.Downloading => next is DirectMusicRequestState.DownloadedWaitingForPlex or
                DirectMusicRequestState.ReadyInPlexamp or DirectMusicRequestState.Failed,
            DirectMusicRequestState.DownloadedWaitingForPlex => next is DirectMusicRequestState.ReadyInPlexamp or
                DirectMusicRequestState.Failed,
            _ => false,
        };

    private List<DirectMusicRequestStatusDTO> Prune(IEnumerable<DirectMusicRequestStatusDTO> source)
    {
        var cutoff = _timeProvider.GetUtcNow().AddDays(-_options.TerminalRetentionDays);
        return source.Where(item => IsActive(item.State) || item.UpdatedAtUtc >= cutoff).ToList();
    }

    private void MakeRoomForOne(List<DirectMusicRequestStatusDTO> records)
    {
        while (records.Count >= _options.MaxRecords)
        {
            var removable = records.Select((record, index) => (record, index))
                .Where(item => !IsActive(item.record.State))
                .OrderBy(item => item.record.UpdatedAtUtc)
                .FirstOrDefault();
            if (removable.record is null)
                throw new InvalidOperationException("The direct music status store is at capacity.");
            records.RemoveAt(removable.index);
        }
    }

    private static bool IsActive(DirectMusicRequestState state) =>
        state is DirectMusicRequestState.DispatchPending or DirectMusicRequestState.Queued or DirectMusicRequestState.Downloading or
            DirectMusicRequestState.DownloadedWaitingForPlex;

    private static void ValidateStoredRecord(DirectMusicRequestStatusDTO record)
    {
        if (record.RequestId == Guid.Empty || record.BatchId == Guid.Empty || record.SearchId == Guid.Empty)
            throw new InvalidDataException("Direct music request, batch, and search IDs are required.");
        if (record.DiscordUserId == 0 || record.DmChannelId == 0)
            throw new InvalidDataException("Discord user and channel IDs are required.");
        ValidateText(record.Username, nameof(record.Username), 200);
        ValidateText(record.RemoteFilename, nameof(record.RemoteFilename), MaximumTextLength);
        ValidateText(record.ArtistName, nameof(record.ArtistName), MaximumTextLength);
        ValidateText(record.TrackTitle, nameof(record.TrackTitle), MaximumTextLength);

        if (record.ExpectedSize <= 0 || record.BytesTransferred < 0 || record.BytesTransferred > record.ExpectedSize)
            throw new InvalidDataException("Direct music request byte counts are invalid.");
        if (record.ExpectedDuration <= TimeSpan.Zero)
            throw new InvalidDataException("A positive expected duration is required.");
        if (record.RequestedAtUtc == default || record.UpdatedAtUtc == default)
            throw new InvalidDataException("Direct music request timestamps are required.");
        if (!Enum.IsDefined(record.State))
            throw new InvalidDataException("The direct music request state is invalid.");
        if (record.TransferId == Guid.Empty)
            throw new InvalidDataException("The transfer ID is invalid.");

        if (record.PlexMediaPath is not null)
            ValidateText(record.PlexMediaPath, nameof(record.PlexMediaPath), MaximumTextLength);
        if (record.FailureCategory is not null)
            ValidateText(record.FailureCategory, nameof(record.FailureCategory), 100);
        if (record.State == DirectMusicRequestState.ReadyInPlexamp && string.IsNullOrWhiteSpace(record.PlexMediaPath))
            throw new InvalidDataException("Plex readiness requires a positive media identity.");
        if (record.State == DirectMusicRequestState.Failed && string.IsNullOrWhiteSpace(record.FailureCategory))
            throw new InvalidDataException("A failed request requires a safe failure category.");
        if (record.State != DirectMusicRequestState.ReadyInPlexamp && record.PlexMediaPath is not null)
            throw new InvalidDataException("Only a Plex-ready request can store a Plex media identity.");
        if (record.State != DirectMusicRequestState.Failed && record.FailureCategory is not null)
            throw new InvalidDataException("Only a failed request can store a failure category.");
    }

    private static void ValidateText(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new InvalidDataException($"The stored {name} is invalid.");
    }


    private static string? TrimOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void EnforceMaximum(IReadOnlyCollection<DirectMusicRequestStatusDTO> records)
    {
        if (records.Count > _options.MaxRecords)
            throw new InvalidDataException("The direct music status store exceeds its record limit.");
    }

    private static void ValidateOptions(DirectMusicRequestStatusOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.StorePath))
            throw new ArgumentException("A direct music status store path is required.", nameof(options));
        if (options.MaxRecords <= 0 || options.TerminalRetentionDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaxFileSizeBytes < 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxFileSizeBytes is too small.");
    }

    private static FileStream AcquireStoreLock(string storePath)
    {
        var primaryPath = Path.GetFullPath(storePath);
        var directory = Path.GetDirectoryName(primaryPath)
            ?? throw new InvalidOperationException("The direct music status path must have a directory.");
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(
                primaryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another process or store instance already owns the direct music status store.", exception);
        }
    }

    private static DirectMusicRequestStatusDTO Clone(DirectMusicRequestStatusDTO source) => source with { };

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

    private sealed class StoreDocument
    {
        public int Version { get; set; }
        public List<DirectMusicRequestStatusDTO> Records { get; set; } = [];
    }
}
