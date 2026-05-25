using ChizuChan.Options;
using System.Collections.Concurrent;
using System.Text.Json;

namespace ChizuChan.Services
{
    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    public sealed class LlmUsageSnapshot
    {
        public string ProviderName { get; set; } = string.Empty;
        public DateOnly Day { get; set; }
        public long RequestsToday { get; set; }
        public long TokensToday { get; set; }
        public long RateLimitFailuresToday { get; set; }
        public long ErrorFailuresToday { get; set; }
        public DateTimeOffset MinuteWindowStartedAt { get; set; }
        public long RequestsThisMinute { get; set; }
        public DateTimeOffset? CooldownUntilUtc { get; set; }
        public DateTimeOffset LastUsedAtUtc { get; set; }
    }

    public sealed class LlmUsageTracker
    {
        private readonly IClock _clock;
        private readonly ConcurrentDictionary<string, LlmUsageSnapshot> _usage = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _fileLock = new();
        private string? _storePath;

        public LlmUsageTracker() : this(new SystemClock())
        {
        }

        public LlmUsageTracker(IClock clock)
        {
            _clock = clock;
        }

        public void UseStore(string? storePath)
        {
            if (string.IsNullOrWhiteSpace(storePath))
                return;

            _storePath = Path.IsPathRooted(storePath)
                ? storePath
                : Path.Combine(AppContext.BaseDirectory, storePath);

            Load();
        }

        public bool CanUse(LlmProviderOptions provider)
        {
            if (!provider.Enabled)
                return false;

            var snapshot = GetCurrentSnapshot(provider.EffectiveName);
            var now = _clock.UtcNow;

            if (snapshot.CooldownUntilUtc is { } cooldown && cooldown > now)
                return false;

            if (provider.DailyRequestLimit > 0 && snapshot.RequestsToday >= provider.DailyRequestLimit)
                return false;

            if (provider.RequestsPerMinuteLimit > 0 && snapshot.RequestsThisMinute >= provider.RequestsPerMinuteLimit)
                return false;

            if (provider.DailyTokenLimit > 0 && snapshot.TokensToday >= provider.DailyTokenLimit)
                return false;

            return true;
        }

        public void RecordSuccess(LlmProviderOptions provider, long promptTokens, long completionTokens)
        {
            var snapshot = GetCurrentSnapshot(provider.EffectiveName);
            snapshot.RequestsToday++;
            snapshot.RequestsThisMinute++;
            snapshot.TokensToday += Math.Max(0, promptTokens) + Math.Max(0, completionTokens);
            snapshot.LastUsedAtUtc = _clock.UtcNow;
            Save();
        }

        public void RecordRateLimit(LlmProviderOptions provider)
        {
            var snapshot = GetCurrentSnapshot(provider.EffectiveName);
            snapshot.RateLimitFailuresToday++;
            snapshot.CooldownUntilUtc = _clock.UtcNow.AddSeconds(Math.Max(1, provider.CooldownSecondsAfterRateLimit));
            Save();
        }

        public void RecordError(LlmProviderOptions provider)
        {
            var snapshot = GetCurrentSnapshot(provider.EffectiveName);
            snapshot.ErrorFailuresToday++;
            Save();
        }

        public LlmUsageSnapshot GetSnapshot(string providerName)
        {
            var current = GetCurrentSnapshot(providerName);
            return new LlmUsageSnapshot
            {
                ProviderName = current.ProviderName,
                Day = current.Day,
                RequestsToday = current.RequestsToday,
                TokensToday = current.TokensToday,
                RateLimitFailuresToday = current.RateLimitFailuresToday,
                ErrorFailuresToday = current.ErrorFailuresToday,
                MinuteWindowStartedAt = current.MinuteWindowStartedAt,
                RequestsThisMinute = current.RequestsThisMinute,
                CooldownUntilUtc = current.CooldownUntilUtc,
                LastUsedAtUtc = current.LastUsedAtUtc
            };
        }

        private LlmUsageSnapshot GetCurrentSnapshot(string providerName)
        {
            var now = _clock.UtcNow;
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var snapshot = _usage.GetOrAdd(providerName, name => NewSnapshot(name, now, today));

            lock (snapshot)
            {
                if (snapshot.Day != today)
                {
                    snapshot.Day = today;
                    snapshot.RequestsToday = 0;
                    snapshot.TokensToday = 0;
                    snapshot.RateLimitFailuresToday = 0;
                    snapshot.ErrorFailuresToday = 0;
                    snapshot.CooldownUntilUtc = null;
                }

                if (now - snapshot.MinuteWindowStartedAt >= TimeSpan.FromMinutes(1))
                {
                    snapshot.MinuteWindowStartedAt = now;
                    snapshot.RequestsThisMinute = 0;
                }
            }

            return snapshot;
        }

        private static LlmUsageSnapshot NewSnapshot(string providerName, DateTimeOffset now, DateOnly today) => new()
        {
            ProviderName = providerName,
            Day = today,
            MinuteWindowStartedAt = now,
            LastUsedAtUtc = now
        };

        private void Load()
        {
            if (_storePath is null || !File.Exists(_storePath))
                return;

            try
            {
                var json = File.ReadAllText(_storePath);
                var snapshots = JsonSerializer.Deserialize<List<LlmUsageSnapshot>>(json) ?? [];
                foreach (var snapshot in snapshots.Where(s => !string.IsNullOrWhiteSpace(s.ProviderName)))
                    _usage[snapshot.ProviderName] = snapshot;
            }
            catch
            {
                // Corrupt usage data should never stop the bot from starting.
            }
        }

        private void Save()
        {
            if (_storePath is null)
                return;

            lock (_fileLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
                    var json = JsonSerializer.Serialize(_usage.Values.OrderBy(v => v.ProviderName), new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_storePath, json);
                }
                catch
                {
                    // Usage persistence is best-effort. Routing still works in-memory.
                }
            }
        }
    }
}
