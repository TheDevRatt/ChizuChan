using System.Collections.Immutable;
using System.Security.Cryptography;
using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public readonly struct MusicSearchSessionToken : IEquatable<MusicSearchSessionToken>
{
    private readonly string? _segment;

    private MusicSearchSessionToken(string segment) => _segment = segment;

    internal static MusicSearchSessionToken Create()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var segment = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new MusicSearchSessionToken(segment);
    }

    public string Segment => _segment ?? string.Empty;

    public bool Equals(MusicSearchSessionToken other) =>
        string.Equals(_segment, other._segment, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is MusicSearchSessionToken other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Segment);
    public static bool operator ==(MusicSearchSessionToken left, MusicSearchSessionToken right) => left.Equals(right);
    public static bool operator !=(MusicSearchSessionToken left, MusicSearchSessionToken right) => !left.Equals(right);
}

/// <summary>
/// A detached immutable selection captured from one exact bound search session.
/// </summary>
public sealed record MusicSearchSelectionSnapshot
{
    internal MusicSearchSelectionSnapshot(
        MusicSearchSessionToken sessionToken,
        ulong ownerUserId,
        ulong dmChannelId,
        ulong sourceMessageId,
        MusicSearchResultPage page)
    {
        SessionToken = sessionToken;
        OwnerUserId = ownerUserId;
        DmChannelId = dmChannelId;
        SourceMessageId = sourceMessageId;
        Page = page;
    }

    public MusicSearchSessionToken SessionToken { get; }
    public ulong OwnerUserId { get; }
    public ulong DmChannelId { get; }
    public ulong SourceMessageId { get; }
    public MusicSearchResultPage Page { get; }
}

public enum MusicSearchSelectionClaimFailure
{
    None,
    Unavailable,
    AlreadyProcessing,
}

/// <summary>
/// An atomic claim on the action for one rendered page.
/// </summary>
public sealed class MusicSearchSelectionClaim
{
    private MusicSearchSelectionClaim(
        MusicSearchSelectionSnapshot? selection,
        MusicSearchSelectionClaimFailure failure,
        MusicSearchSessionToken sessionToken,
        int index)
    {
        Selection = selection!;
        Failure = failure;
        SessionToken = sessionToken;
        Index = index;
    }

    public MusicSearchSelectionSnapshot Selection { get; }
    public MusicSearchSelectionClaimFailure Failure { get; }
    internal MusicSearchSessionToken SessionToken { get; }
    internal int Index { get; }
    internal bool Acquired => Failure == MusicSearchSelectionClaimFailure.None;

    internal static MusicSearchSelectionClaim Succeeded(
        MusicSearchSelectionSnapshot selection,
        MusicSearchSessionToken token,
        int index) => new(selection, MusicSearchSelectionClaimFailure.None, token, index);

    internal static MusicSearchSelectionClaim Rejected(MusicSearchSelectionClaimFailure failure) =>
        new(null, failure, default, -1);
}

/// <summary>
/// Opaque proof of a prepared render. It can only commit against the same live session and rendered page.
/// </summary>
public sealed class MusicSearchRenderCommit
{
    internal MusicSearchRenderCommit(
        ulong ownerUserId,
        ulong dmChannelId,
        ulong sourceMessageId,
        MusicSearchSessionToken sessionToken,
        int expectedRenderedIndex,
        int targetIndex)
    {
        OwnerUserId = ownerUserId;
        DmChannelId = dmChannelId;
        SourceMessageId = sourceMessageId;
        SessionToken = sessionToken;
        ExpectedRenderedIndex = expectedRenderedIndex;
        TargetIndex = targetIndex;
    }

    internal ulong OwnerUserId { get; }
    internal ulong DmChannelId { get; }
    internal ulong SourceMessageId { get; }
    internal MusicSearchSessionToken SessionToken { get; }
    internal int ExpectedRenderedIndex { get; }
    internal int TargetIndex { get; }
}

public class MusicSearchSessionService : IMusicSearchSessionService
{
    private const int MaximumUnifiedResults = 15;
    private const int MaximumLegacyLidarrResults = 10;
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

    public MusicSearchSessionToken SaveResults(
        ulong userId,
        ulong dmChannelId,
        string query,
        IEnumerable<MusicSearchResultPage> pages,
        bool lidarrAvailable = true,
        bool youtubeAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(pages);

        var savedPages = pages.Take(MaximumUnifiedResults).ToImmutableArray();
        if (savedPages.Any(page => page is null))
            throw new ArgumentException("Result pages cannot contain null values.", nameof(pages));

        var now = _timeProvider.GetUtcNow();
        var token = MusicSearchSessionToken.Create();
        lock (_syncRoot)
        {
            RemoveExpiredSessions(now);
            _sessions[userId] = new SearchSession
            {
                Query = query.Trim(),
                Pages = savedPages,
                RenderedIndex = 0,
                OwnerUserId = userId,
                DmChannelId = dmChannelId,
                SourceMessageId = 0,
                LidarrAvailable = lidarrAvailable,
                YouTubeAvailable = youtubeAvailable,
                SavedAt = now,
                Sequence = _nextSequence++,
                Token = token,
            };

            var maxSessions = Math.Max(0, _options.MaxSessions);
            while (_sessions.Count > maxSessions)
            {
                var oldest = _sessions.MinBy(pair => (pair.Value.SavedAt, pair.Value.Sequence));
                _sessions.Remove(oldest.Key);
            }
        }

        return token;
    }

    public bool BindMessage(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        MusicSearchSessionToken token)
    {
        if (sourceMessageId == 0)
            return false;

        lock (_syncRoot)
        {
            if (!TryGetLiveSession(userId, out var session) ||
                session.DmChannelId != dmChannelId ||
                session.Token != token)
            {
                return false;
            }

            if (session.SourceMessageId != 0 && session.SourceMessageId != sourceMessageId)
                return false;

            if (session.SourceMessageId == 0)
                session.RenderedIndex = 0;
            session.SourceMessageId = sourceMessageId;
            return true;
        }
    }

    public bool GetUnboundCurrent(
        ulong userId,
        ulong dmChannelId,
        MusicSearchSessionToken token,
        out MusicSearchSessionSnapshot session)
    {
        lock (_syncRoot)
        {
            if (!TryGetLiveSession(userId, out var stored) ||
                stored.OwnerUserId != userId ||
                stored.DmChannelId != dmChannelId ||
                stored.SourceMessageId != 0 ||
                stored.Token != token)
            {
                session = null!;
                return false;
            }

            session = Snapshot(stored);
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

    public bool TryClaimRenderedSelection(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        string actionTokenSegment,
        int index,
        out MusicSearchSelectionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(actionTokenSegment);

        lock (_syncRoot)
        {
            if (!TryGetBoundSession(userId, dmChannelId, sourceMessageId, out var stored) ||
                !string.Equals(stored.Token.Segment, actionTokenSegment, StringComparison.Ordinal) ||
                index < 0 ||
                index >= stored.Pages.Length ||
                stored.RenderedIndex != index)
            {
                claim = MusicSearchSelectionClaim.Rejected(MusicSearchSelectionClaimFailure.Unavailable);
                return false;
            }

            if (!stored.InFlightActionIndexes.Add(index))
            {
                claim = MusicSearchSelectionClaim.Rejected(MusicSearchSelectionClaimFailure.AlreadyProcessing);
                return false;
            }

            var selection = new MusicSearchSelectionSnapshot(
                stored.Token,
                stored.OwnerUserId,
                stored.DmChannelId,
                stored.SourceMessageId,
                stored.Pages[index]);
            claim = MusicSearchSelectionClaim.Succeeded(selection, stored.Token, index);
            return true;
        }
    }

    public void ReleaseSelectionClaim(MusicSearchSelectionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!claim.Acquired)
            return;

        lock (_syncRoot)
        {
            var ownerUserId = claim.Selection.OwnerUserId;
            if (_sessions.TryGetValue(ownerUserId, out var stored) && stored.Token == claim.SessionToken)
                stored.InFlightActionIndexes.Remove(claim.Index);
        }
    }

    public bool PreparePrevious(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        out MusicSearchSessionSnapshot session,
        out MusicSearchRenderCommit renderCommit) =>
        PrepareMove(userId, dmChannelId, sourceMessageId, -1, out session, out renderCommit);

    public bool PrepareNext(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        out MusicSearchSessionSnapshot session,
        out MusicSearchRenderCommit renderCommit) =>
        PrepareMove(userId, dmChannelId, sourceMessageId, 1, out session, out renderCommit);

    public bool CommitRendered(MusicSearchRenderCommit renderCommit)
    {
        ArgumentNullException.ThrowIfNull(renderCommit);

        lock (_syncRoot)
        {
            if (!TryGetBoundSession(
                    renderCommit.OwnerUserId,
                    renderCommit.DmChannelId,
                    renderCommit.SourceMessageId,
                    out var stored) ||
                stored.Token != renderCommit.SessionToken ||
                stored.RenderedIndex != renderCommit.ExpectedRenderedIndex ||
                renderCommit.TargetIndex < 0 ||
                renderCommit.TargetIndex >= stored.Pages.Length)
            {
                return false;
            }

            stored.RenderedIndex = renderCommit.TargetIndex;
            return true;
        }
    }

    public void SaveResults(ulong userId, IEnumerable<LidarrAlbumDTO> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        SaveResults(userId, 0, string.Empty, results
            .Take(MaximumLegacyLidarrResults)
            .Select(MusicSearchResultPage.FromLidarr));
    }

    public bool TryGetResult(ulong userId, int oneBasedResultNumber, out LidarrAlbumDTO album)
    {
        album = null!;
        if (oneBasedResultNumber < 1 || oneBasedResultNumber > MaximumLegacyLidarrResults)
            return false;

        lock (_syncRoot)
        {
            if (!TryGetLiveSession(userId, out var session) || oneBasedResultNumber > session.Pages.Length)
                return false;

            var page = session.Pages[oneBasedResultNumber - 1];
            if (page.LidarrAlbum is null)
                return false;

            album = page.LidarrAlbum.ToDto();
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

    private bool PrepareMove(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        int offset,
        out MusicSearchSessionSnapshot session,
        out MusicSearchRenderCommit renderCommit)
    {
        lock (_syncRoot)
        {
            if (!TryGetBoundSession(userId, dmChannelId, sourceMessageId, out var stored) || stored.Pages.Length == 0)
            {
                session = null!;
                renderCommit = null!;
                return false;
            }

            var targetIndex = (stored.RenderedIndex + offset + stored.Pages.Length) % stored.Pages.Length;
            session = Snapshot(stored, targetIndex);
            renderCommit = new MusicSearchRenderCommit(
                stored.OwnerUserId,
                stored.DmChannelId,
                stored.SourceMessageId,
                stored.Token,
                stored.RenderedIndex,
                targetIndex);
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

    private static MusicSearchSessionSnapshot Snapshot(SearchSession session, int? index = null) => new(
        session.Query,
        session.Pages,
        index ?? session.RenderedIndex,
        session.OwnerUserId,
        session.DmChannelId,
        session.SourceMessageId,
        session.LidarrAvailable,
        session.YouTubeAvailable,
        session.Token.Segment);

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
        public required ImmutableArray<MusicSearchResultPage> Pages { get; init; }
        public required MusicSearchSessionToken Token { get; init; }
        public int RenderedIndex { get; set; }
        public HashSet<int> InFlightActionIndexes { get; } = [];
        public ulong OwnerUserId { get; init; }
        public ulong DmChannelId { get; init; }
        public ulong SourceMessageId { get; set; }
        public bool LidarrAvailable { get; init; }
        public bool YouTubeAvailable { get; init; }
        public DateTimeOffset SavedAt { get; init; }
        public long Sequence { get; init; }
    }
}
