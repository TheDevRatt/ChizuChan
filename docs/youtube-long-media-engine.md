# Finite long-media engine

## Configuration migration

Existing deployments retain explicitly configured old values. Updating code alone does not disable the deployed 900-second duration, 300-second elapsed, or 100-MiB file policies. The release owner must update only these reviewed, nonsecret YouTubeMusicDownload settings in the protected live configuration:

```json
{
  "MaxDurationSeconds": 0,
  "DownloadTimeoutSeconds": 0,
  "MaxFileSizeBytes": 0,
  "MinimumFreeSpaceBytes": 1073741824,
  "StalledWorkTimeoutSeconds": 300,
  "ResourceMonitoringIntervalMilliseconds": 1000
}
```

No live settings are changed by this branch. Preserve Enabled, tool locations, exclusive-root policy, library location, secrets and unrelated settings.

| Setting | Meaning |
| --- | --- |
| MaxDurationSeconds | Optional maximum *playback duration*. Default 0. Zero/negative disables the ceiling. Positive values are honored exactly, without the old six-hour clamp. |
| MaxFileSizeBytes | Optional new acquired/tagged file size ceiling. Default 0. Zero/negative disables it. Positive values are honored exactly, without the old one-GiB clamp. Existing library files are not admission candidates and are never quarantined for exceeding a lowered policy. |
| DownloadTimeoutSeconds | Optional elapsed ceiling **per child process**, starting after its concurrency slot is acquired. Default 0. Zero/negative disables it. Positive values are not clamped. There is no whole-job elapsed ceiling. Prefer the progress watchdog instead. |
| MinimumFreeSpaceBytes | Actual free-space floor on the working library volume. Default one GiB of headroom for filesystem metadata, other writers, and writes between samples. This is **not** a maximum media size. Zero/negative disables the floor, which is not recommended. Select headroom for the volume's write rate and other workloads. |
| StalledWorkTimeoutSeconds | Default 300 seconds without stdout/stderr, top-level staging file size/mtime change, or parent CPU-time advance. Any such activity resets the watchdog. Healthy work can run indefinitely. Zero/negative disables it. A silent stalled process is reported as stalled, not as too-long media. |
| ResourceMonitoringIntervalMilliseconds | Default 1000 ms. Positive values are honored exactly; nonpositive values use 1000 ms. Shorter intervals reduce storage overshoot but cost more filesystem sampling. |

Storage is checked before starting writers, during work, and after exit. Read-only media verification is exempt from the storage admission floor. No duration-derived size estimate rejects an otherwise finite track. Download, extraction, cover conversion and stream-copy tagging can temporarily occupy multiple files, so plan for more than the final library size. A sampled reserve cannot prevent another writer from consuming the volume between samples and is not a filesystem quota. Do not grant other accounts write access to the dedicated library. Administrators must monitor crash-orphaned staging and quarantine retention; ordinary cancellation/failure cleans the current validated staging directory.

The existing per-root writer lock remains held through cleanup. Its RootLockTimeoutSeconds is a *queue acquisition* budget (30 seconds by default, existing 1..300-second normalization), not a processing cutoff. A process-wide semaphore allows two media child processes. Caller/shutdown cancellation applies while waiting for either an in-memory video lock or a process slot, and during subprocess work. The delivery/job layer owns admission queue bounds and durable Discord lifecycle.

## Eligibility and acquisition

- Canonical single-video URL, video-id matching, youtube extractor checks, no playlists, ignored local yt-dlp config and ArgumentList/no-shell execution remain in place.
- Positive finite numeric duration is required. Missing, malformed, zero and nonfinite duration are invalid metadata, not "too long".
- `not_live` and finite completed `was_live` archives are accepted. Active, upcoming, post-live/not-yet-completed and unknown statuses are rejected. The acquisition filter rechecks these properties to cover status changes after metadata probing.
- Two yt-dlp match filters implement OR between completed statuses; clauses within each filter are AND-ed. Tests execute the real yt-dlp parser, not just string assertions.
- Disabled policies do not emit yt-dlp maximum-size or upper-duration filters. Optional positive ceilings appear exactly as configured. Audio-only formats are preferred, with yt-dlp responsible for the required M4A extraction/conversion.

## Subprocess and audio contracts

`IYouTubeMusicActionHandler` is unchanged. The six-argument `YouTubeDownloadToolInvocation` constructor remains source-compatible and defaults to `OutputMode = Metadata`. This keeps stdout strictly bounded for existing structured-output callers. `OutputMode = Diagnostics` drains stdout indefinitely, retaining only a bounded tail. Stderr always drains indefinitely with bounded tail retention. Exceeding total progress output is not fatal. MaxMetadataBytes retains its historical **decoded character** unit despite its name, bounded to 4 Ki..1 Mi characters. Handler diagnostic retention is 64 Ki stdout/32 Ki stderr, with 8 Ki per stream during verification; pipe buffers are fixed-size.

Resource settings are additive init properties on an invocation. The production tool keeps its parameterless constructor; an additional free-space-probe constructor supports deterministic low-disk tests. Consumers linking source instead of the production project must include `Services/YouTubeLongMediaResourceMonitor.cs`.

On cancellation, failure, stall or explicit elapsed expiry, the runner requests `.Kill(entireProcessTree: true)`, closes redirected pipes and observes cleanup with a two-second teardown budget, then releases the slot. This is standard .NET tree termination, not a hostile-process sandbox. A parent that exits before cleanup can make detached descendants untrackable; a kill-on-close job/container would be needed for hostile self-detaching tools. The tools must be trusted. Windows uses ArgumentList, CreateNoWindow and `NUL` for the null sink. Linux child-tree cancellation is exercised; native Windows runtime execution remains a release verification requirement.

Tagging uses `-c:a copy` on the M4A audio rather than AAC re-encoding. Authoritative title/artist/album/source tags and replacement cover are attached. If no replacement cover exists, an existing cover is retained. Encoded AAC packet SHA-256 hashes are unchanged in real-media tests.

Verification decodes the **entire** audio stream through EOF with ffmpeg `-xerror`, using bounded `-progress pipe:1` output. Success requires clean error output, `progress=end`, positive decoded duration, and for new acquisitions a duration within two seconds of authoritative metadata. The fixed tolerance accommodates codec/container rounding and does not grow with media length. This costs one linear decode, not an additional lossy encode, and has no default elapsed cap. Corrupted tails and a three-second audio file advertised as seven hours cannot be promoted. Existing legacy indexed items have no stored expected duration, so reuse checks full decodability and positive duration rather than comparing absent upstream metadata.

## Tests and release integration

See `tests/ChizuChan.LongMedia.Engine.Tests/README.md`. This branch does not modify Program.cs, commands, CI or other test projects. Integrate the engine suite into CI together with the independent regression and durable delivery lanes. Run the final integrated Windows Release publish and native Windows subprocess/audio tests before promotion. Never interpret these isolated tests as a reproduced upstream incident or as production deployment verification.
