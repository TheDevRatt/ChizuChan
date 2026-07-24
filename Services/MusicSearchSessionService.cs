using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public class MusicSearchSessionService : IMusicSearchSessionService
{
    private const int MaximumResults = 10;
    private readonly Dictionary<ulong, SearchSession> _sessions = [];
    private readonly object _syncRoot = new();
    private readonly LidarrOptions _options;
    private readonly TimeProvider _timeProvider;
    private long _nextSequence;

    public MusicSearchSessionService(
        IOptions<LidarrOptions>? options = null,
        TimeProvider? timeProvider = null)
    {
        _options = options?.Value ?? new LidarrOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void SaveResults(ulong userId, IEnumerable<LidarrAlbumDTO> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var savedResults = results.Take(MaximumResults).ToArray();
        var now = _timeProvider.GetUtcNow();

        lock (_syncRoot)
        {
            RemoveExpiredSessions(now);
            _sessions[userId] = new SearchSession(savedResults, now, _nextSequence++);

            var maxSessions = Math.Max(0, _options.MaxSessions);
            while (_sessions.Count > maxSessions)
            {
                var oldest = _sessions.MinBy(pair => (pair.Value.SavedAt, pair.Value.Sequence));
                _sessions.Remove(oldest.Key);
            }
        }
    }

    public bool TryGetResult(ulong userId, int oneBasedResultNumber, out LidarrAlbumDTO album)
    {
        album = null!;
        if (oneBasedResultNumber < 1 || oneBasedResultNumber > MaximumResults)
            return false;

        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(userId, out var session))
                return false;

            if (IsExpired(session, _timeProvider.GetUtcNow()))
            {
                _sessions.Remove(userId);
                return false;
            }

            if (oneBasedResultNumber > session.Results.Count)
                return false;

            album = session.Results[oneBasedResultNumber - 1];
            return true;
        }
    }

    public void ClearResults(ulong userId)
    {
        lock (_syncRoot)
        {
            _sessions.Remove(userId);
        }
    }

    private void RemoveExpiredSessions(DateTimeOffset now)
    {
        foreach (var userId in _sessions
                     .Where(pair => IsExpired(pair.Value, now))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _sessions.Remove(userId);
        }
    }

    private bool IsExpired(SearchSession session, DateTimeOffset now) =>
        now - session.SavedAt >= TimeSpan.FromMinutes(_options.SessionLifetimeMinutes);

    private sealed record SearchSession(
        IReadOnlyList<LidarrAlbumDTO> Results,
        DateTimeOffset SavedAt,
        long Sequence);
}
