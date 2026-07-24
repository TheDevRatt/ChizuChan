using ChizuChan.Services.Interfaces;

namespace ChizuChan.Services;

public sealed class MusicSearchInteractionGate : IMusicSearchInteractionGate
{
    private readonly Dictionary<SessionKey, GateEntry> _entries = [];
    private readonly object _syncRoot = new();

    public async ValueTask<IAsyncDisposable> EnterAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = new SessionKey(userId, dmChannelId, sourceMessageId);
        GateEntry entry;
        lock (_syncRoot)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new GateEntry();
                _entries.Add(key, entry);
            }

            // Count holders and waiters before waiting so an entry cannot be removed
            // while any participant can still acquire its semaphore.
            entry.ReferenceCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            RemoveReference(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Exit(SessionKey key, GateEntry entry)
    {
        entry.Semaphore.Release();
        RemoveReference(key, entry);
    }

    private void RemoveReference(SessionKey key, GateEntry entry)
    {
        lock (_syncRoot)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 &&
                _entries.TryGetValue(key, out var current) &&
                ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private readonly record struct SessionKey(
        ulong UserId,
        ulong DmChannelId,
        ulong SourceMessageId);

    private sealed class GateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class Releaser(
        MusicSearchInteractionGate owner,
        SessionKey key,
        GateEntry entry) : IAsyncDisposable
    {
        private MusicSearchInteractionGate? _owner = owner;

        public ValueTask DisposeAsync()
        {
            var currentOwner = Interlocked.Exchange(ref _owner, null);
            currentOwner?.Exit(key, entry);
            return ValueTask.CompletedTask;
        }
    }
}
