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

    public void SaveResults(
        ulong userId,
        ulong dmChannelId,
        string query,
        IEnumerable<MusicSearchResultPage> pages,
        bool lidarrAvailable = true,
        bool youtubeAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(pages);

        var savedPages = pages.Take(MaximumResults).ToArray();
        if (savedPages.Any(page => page is null))
            throw new ArgumentException("Result pages cannot contain null values.", nameof(pages));

        var now = _timeProvider.GetUtcNow();
        lock (_syncRoot)
        {
            RemoveExpiredSessions(now);
            _sessions[userId] = new SearchSession
            {
                Query = query.Trim(),
                Pages = savedPages,
                CurrentIndex = 0,
                OwnerUserId = userId,
                DmChannelId = dmChannelId,
                SourceMessageId = 0,
                LidarrAvailable = lidarrAvailable,
                YouTubeAvailable = youtubeAvailable,
                SavedAt = now,
                Sequence = _nextSequence++,
            };

            var maxSessions = Math.Max(0, _options.MaxSessions);
            while (_sessions.Count > maxSessions)
            {
                var oldest = _sessions.MinBy(pair => (pair.Value.SavedAt, pair.Value.Sequence));
                _sessions.Remove(oldest.Key);
            }
        }
    }

    public bool BindMessage(ulong userId, ulong dmChannelId, ulong sourceMessageId)
    {
        if (sourceMessageId == 0)
            return false;

        lock (_syncRoot)
        {
            if (!TryGetLiveSession(userId, out var session) || session.DmChannelId != dmChannelId)
                return false;

            if (session.SourceMessageId != 0 && session.SourceMessageId != sourceMessageId)
                return false;

            session.SourceMessageId = sourceMessageId;
            return true;
        }
    }

    public bool GetCurrent(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        out MusicSearchSessionSnapshot session)
    {
        lock (_syncRoot)
        {
            if (!TryGetBoundSession(userId, dmChannelId, sourceMessageId, out var stored))
            {
                session = null!;
                return false;
            }

            session = Snapshot(stored);
            return true;
        }
    }

    public bool MovePrevious(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        out MusicSearchSessionSnapshot session) =>
        Move(userId, dmChannelId, sourceMessageId, -1, out session);

    public bool MoveNext(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        out MusicSearchSessionSnapshot session) =>
        Move(userId, dmChannelId, sourceMessageId, 1, out session);

    public void SaveResults(ulong userId, IEnumerable<LidarrAlbumDTO> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        SaveResults(userId, 0, string.Empty, results.Select(MusicSearchResultPage.FromLidarr));
    }

    public bool TryGetResult(ulong userId, int oneBasedResultNumber, out LidarrAlbumDTO album)
    {
        album = null!;
        if (oneBasedResultNumber < 1 || oneBasedResultNumber > MaximumResults)
            return false;

        lock (_syncRoot)
        {
            if (!TryGetLiveSession(userId, out var session) || oneBasedResultNumber > session.Pages.Count)
                return false;

            var page = session.Pages[oneBasedResultNumber - 1];
            if (page.LidarrAlbum is null)
                return false;

            album = page.LidarrAlbum;
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

    private bool Move(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        int offset,
        out MusicSearchSessionSnapshot session)
    {
        lock (_syncRoot)
        {
            if (!TryGetBoundSession(userId, dmChannelId, sourceMessageId, out var stored) || stored.Pages.Count == 0)
            {
                session = null!;
                return false;
            }

            stored.CurrentIndex = (stored.CurrentIndex + offset + stored.Pages.Count) % stored.Pages.Count;
            session = Snapshot(stored);
            return true;
        }
    }

    private bool TryGetBoundSession(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        out SearchSession session)
    {
        if (!TryGetLiveSession(userId, out session))
            return false;

        return sourceMessageId != 0 &&
               session.OwnerUserId == userId &&
               session.DmChannelId == dmChannelId &&
               session.SourceMessageId == sourceMessageId;
    }

    private bool TryGetLiveSession(ulong userId, out SearchSession session)
    {
        if (!_sessions.TryGetValue(userId, out session!))
            return false;

        if (!IsExpired(session, _timeProvider.GetUtcNow()))
            return true;

        _sessions.Remove(userId);
        session = null!;
        return false;
    }

    private static MusicSearchSessionSnapshot Snapshot(SearchSession session) => new(
        session.Query,
        session.Pages,
        session.CurrentIndex,
        session.OwnerUserId,
        session.DmChannelId,
        session.SourceMessageId,
        session.LidarrAvailable,
        session.YouTubeAvailable);

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

    private sealed class SearchSession
    {
        public required string Query { get; init; }
        public required IReadOnlyList<MusicSearchResultPage> Pages { get; init; }
        public int CurrentIndex { get; set; }
        public ulong OwnerUserId { get; init; }
        public ulong DmChannelId { get; init; }
        public ulong SourceMessageId { get; set; }
        public bool LidarrAvailable { get; init; }
        public bool YouTubeAvailable { get; init; }
        public DateTimeOffset SavedAt { get; init; }
        public long Sequence { get; init; }
    }
}
