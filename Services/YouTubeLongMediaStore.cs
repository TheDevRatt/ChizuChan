using System.Text.Json;
using System.Text.RegularExpressions;
using ChizuChan.Options;
using Microsoft.Extensions.Options;

namespace ChizuChan.Services;

/// <summary>Single-host atomic journal. The lifetime lock prevents overlapping hosts from executing a job twice.</summary>
public sealed class YouTubeLongMediaStore : IDisposable
{
    public const string InterruptedMessage = "Download interrupted by host shutdown or restart. It may already have imported; check Plex before submitting again.";
    private readonly object _gate = new();
    private readonly YouTubeLongMediaDeliveryOptions _options;
    private readonly FileStream _lease;
    private List<YouTubeLongMediaJob> _jobs;

    public YouTubeLongMediaStore(IOptions<YouTubeLongMediaDeliveryOptions> options)
    {
        _options = options.Value;
        if (!Path.IsPathFullyQualified(_options.StorePath) || _options.MaxPendingJobs < 1 ||
            _options.MaxStoredJobs < 0 ||
            _options.Concurrency < 1 || _options.Concurrency > _options.MaxPendingJobs ||
            _options.PollIntervalMilliseconds < 1 || _options.DeliveryRetrySeconds < 1)
            throw new ArgumentException("Invalid YouTube delivery storage or resource policy.");
        Directory.CreateDirectory(Path.GetDirectoryName(_options.StorePath)!);
        _lease = new FileStream(_options.StorePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (File.Exists(_options.StorePath))
            {
                _jobs = JsonSerializer.Deserialize<List<YouTubeLongMediaJob>>(File.ReadAllBytes(_options.StorePath))
                    ?? throw new InvalidDataException("Invalid YouTube delivery journal.");
                if (_jobs.Select(j => j.Id).Distinct().Count() != _jobs.Count ||
                    _jobs.Any(j => !Regex.IsMatch(j.Id, "\\A[a-f0-9]{24}\\z") || j.OwnerUserId == 0 ||
                        !IsVideoId(j.VideoId) || !Enum.IsDefined(j.State) || j.Message.Length > 1000))
                    throw new InvalidDataException("Invalid YouTube delivery journal records.");
            }
            else _jobs = [];
            // Do not blindly replay Running: promotion may have happened before the process died.
            var recovered = _jobs.Select(j => j.State == YouTubeLongMediaState.Running
                ? j with { State = YouTubeLongMediaState.Interrupted, Message = InterruptedMessage } : j).ToList();
            Persist(recovered);
        }
        catch { _lease.Dispose(); throw; }
    }

    public static bool IsVideoId(string? id) => id is not null && Regex.IsMatch(id, "\\A[A-Za-z0-9_-]{11}\\z");

    public (YouTubeLongMediaJob? Job, string? Error) Admit(ulong owner, string videoId)
    {
        lock (_gate)
        {
            // Per-owner identity also joins direct URL and search-card submissions. Never expose another owner's job.
            var existing = _jobs.LastOrDefault(j => j.OwnerUserId == owner && j.VideoId == videoId &&
                j.State is YouTubeLongMediaState.Queued or YouTubeLongMediaState.Running);
            if (existing is not null) return (existing, null);
            if (_jobs.Count(j => !j.IsTerminal) >= _options.MaxPendingJobs)
                return (null, "The YouTube download queue is busy. Please try again later.");
            // Terminal success is history, not proof that the file still exists. A new explicit
            // request re-enters the engine, whose locked index check safely avoids duplicate imports.
            var job = new YouTubeLongMediaJob { Id = Guid.NewGuid().ToString("N")[..24], OwnerUserId = owner, VideoId = videoId };
            Persist([.. _jobs, job]);
            return (job, null);
        }
    }

    public YouTubeLongMediaJob? GetOwned(ulong owner, string? id = null)
    {
        lock (_gate) return _jobs.LastOrDefault(j => j.OwnerUserId == owner && (string.IsNullOrEmpty(id) || j.Id == id));
    }

    public YouTubeLongMediaJob? ClaimNext()
    {
        lock (_gate)
        {
            var next = _jobs.FirstOrDefault(j => j.State == YouTubeLongMediaState.Queued);
            if (next is null) return null;
            var running = next with { State = YouTubeLongMediaState.Running, Message = "Download is running." };
            Replace(running);
            return running;
        }
    }

    public void Finish(string id, YouTubeLongMediaState state, string message)
    {
        lock (_gate)
        {
            var job = _jobs.Single(j => j.Id == id);
            Replace(job with { State = state, Message = Bound(message), NextDeliveryAt = default });
        }
    }

    public YouTubeLongMediaJob? NextDelivery(DateTimeOffset now)
    {
        lock (_gate) return _jobs.FirstOrDefault(j => j.IsTerminal && j.DeliveredMessageId is null && j.NextDeliveryAt <= now);
    }

    public void RecordDelivery(string id, ulong? messageId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var job = _jobs.Single(j => j.Id == id);
            Replace(job with
            {
                DeliveredMessageId = messageId,
                DeliveryAttempts = job.DeliveryAttempts == int.MaxValue ? int.MaxValue : job.DeliveryAttempts + 1,
                DeliveryError = messageId is null ? "Could not deliver the DM; retrying. Use /youtube_job for status." : null,
                NextDeliveryAt = now.AddSeconds(_options.DeliveryRetrySeconds),
            });
        }
    }

    public static string Bound(string? message) => string.IsNullOrWhiteSpace(message)
        ? "No further result details are available." : message[..Math.Min(message.Length, 500)];

    private void Replace(YouTubeLongMediaJob replacement) => Persist(_jobs.Select(j => j.Id == replacement.Id ? replacement : j).ToList());

    private void Persist(List<YouTubeLongMediaJob> jobs)
    {
        // Rotate only acknowledged terminal history. Neither active work nor results awaiting
        // delivery are disposable, and accumulated history must never deny a new download.
        var retained = jobs.Where(j => j.IsTerminal && j.DeliveredMessageId is not null)
            .TakeLast(_options.MaxStoredJobs).Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
        jobs = jobs.Where(j => !j.IsTerminal || j.DeliveredMessageId is null || retained.Contains(j.Id)).ToList();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(jobs);
        var temp = _options.StorePath + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, _options.StorePath, overwrite: true);
            _jobs = jobs; // Never expose an acknowledgment or new state until persisted.
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Dispose() => _lease.Dispose();
}
