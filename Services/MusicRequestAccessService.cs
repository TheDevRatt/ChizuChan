using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

public sealed class MusicRequestAccessService : IMusicRequestAccessService
{
    private static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(1);

    private readonly HashSet<ulong> _allowedUserIds;
    private readonly TimeSpan _searchCooldown;
    private readonly TimeSpan _requestCooldown;
    private readonly int _globalOperationsPerMinute;
    private readonly TimeProvider _timeProvider;
    private readonly object _syncRoot = new();
    private readonly Queue<DateTimeOffset> _globalOperations = new();
    private readonly Dictionary<ulong, UserOperations> _userOperations = [];

    public MusicRequestAccessService(
        IOptions<LidarrOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var values = options.Value;
        _allowedUserIds = values.AllowedUserIds?.ToHashSet() ?? [];
        _searchCooldown = TimeSpan.FromSeconds(Math.Max(0, values.SearchCooldownSeconds));
        _requestCooldown = TimeSpan.FromSeconds(Math.Max(0, values.RequestCooldownSeconds));
        _globalOperationsPerMinute = Math.Max(0, values.GlobalOperationsPerMinute);
        _timeProvider = timeProvider;
    }

    public MusicRequestAccessResult CheckAccess(ulong userId, MusicRequestOperation operation)
    {
        if (!_allowedUserIds.Contains(userId))
            return MusicRequestAccessResult.Unauthorized();

        var now = _timeProvider.GetUtcNow();
        lock (_syncRoot)
        {
            PruneExpiredState(now);

            var cooldown = GetCooldown(operation);
            if (_userOperations.TryGetValue(userId, out var userOperations))
            {
                var lastOperation = userOperations.Get(operation);
                if (lastOperation is not null)
                {
                    var retryAfter = lastOperation.Value + cooldown - now;
                    if (retryAfter > TimeSpan.Zero)
                        return MusicRequestAccessResult.RateLimited(retryAfter);
                }
            }

            if (_globalOperations.Count >= _globalOperationsPerMinute)
            {
                var retryAfter = _globalOperations.Count == 0
                    ? GlobalWindow
                    : _globalOperations.Peek() + GlobalWindow - now;
                return MusicRequestAccessResult.RateLimited(
                    retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.Zero);
            }

            _globalOperations.Enqueue(now);
            if (cooldown > TimeSpan.Zero)
            {
                userOperations ??= new UserOperations();
                userOperations.Set(operation, now);
                _userOperations[userId] = userOperations;
            }

            return MusicRequestAccessResult.Allowed();
        }
    }

    private TimeSpan GetCooldown(MusicRequestOperation operation) => operation switch
    {
        MusicRequestOperation.Search => _searchCooldown,
        MusicRequestOperation.Request => _requestCooldown,
        MusicRequestOperation.Download => _requestCooldown,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private void PruneExpiredState(DateTimeOffset now)
    {
        var globalCutoff = now - GlobalWindow;
        while (_globalOperations.TryPeek(out var timestamp) && timestamp <= globalCutoff)
            _globalOperations.Dequeue();

        foreach (var userId in _userOperations
                     .Where(pair => !pair.Value.HasActiveCooldown(now, _searchCooldown, _requestCooldown))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _userOperations.Remove(userId);
        }
    }

    private sealed class UserOperations
    {
        private DateTimeOffset? _lastSearch;
        private DateTimeOffset? _lastRequest;
        private DateTimeOffset? _lastDownload;

        public DateTimeOffset? Get(MusicRequestOperation operation) => operation switch
        {
            MusicRequestOperation.Search => _lastSearch,
            MusicRequestOperation.Request => _lastRequest,
            MusicRequestOperation.Download => _lastDownload,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

        public void Set(MusicRequestOperation operation, DateTimeOffset timestamp)
        {
            switch (operation)
            {
                case MusicRequestOperation.Search:
                    _lastSearch = timestamp;
                    break;
                case MusicRequestOperation.Request:
                    _lastRequest = timestamp;
                    break;
                case MusicRequestOperation.Download:
                    _lastDownload = timestamp;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }

        public bool HasActiveCooldown(
            DateTimeOffset now,
            TimeSpan searchCooldown,
            TimeSpan requestCooldown) =>
            IsActive(_lastSearch, searchCooldown, now) ||
            IsActive(_lastRequest, requestCooldown, now) ||
            IsActive(_lastDownload, requestCooldown, now);

        private static bool IsActive(
            DateTimeOffset? timestamp,
            TimeSpan cooldown,
            DateTimeOffset now) =>
            timestamp is not null && timestamp.Value + cooldown > now;
    }
}