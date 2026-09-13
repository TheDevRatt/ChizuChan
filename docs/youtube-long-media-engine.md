# Finite long-media engine

## Configuration migration

Code alone does not disable explicitly configured legacy limits. The release owner must update only these allowlisted, nonsecret `YouTubeMusicDownload` fields in the protected live configuration:

```json
{
  "MaxDurationSeconds": 0,
  "DownloadTimeoutSeconds": 0,
  "MaxFileSizeBytes": 0,
  "StalledWorkTimeoutSeconds": 300,
  "ResourceMonitoringIntervalMilliseconds": 1000
}
```

Remove any obsolete `MinimumFreeSpaceBytes` field. It is no longer an application option. No library free-space floor, reserve, staging quota or aggregate media cap exists. Actual filesystem failures are handled, not predicted by an admission policy. Preserve Enabled, tool/library locations, exclusive-root policy, secrets and unrelated settings. This candidate changes no live configuration.

- Duration, new-file size and per-process elapsed caps default to zero (disabled). Nonpositive values disable them; explicit positive values are honored without hidden upper clamps. There is no whole-job elapsed ceiling. Existing files are exempt from acquisition size policies.
- StalledWorkTimeoutSeconds defaults to 300. Output, staging file changes, or parent CPU activity reset the watchdog. Healthy work has no default elapsed cutoff. Nonpositive disables stall detection.
- ResourceMonitoringIntervalMilliseconds defaults to 1000. Positive values are honored; nonpositive values use the default. Sampling tracks activity, never available storage.
- RootLockTimeoutSeconds remains a queue-lock acquisition budget (default 30, normalized to 1..300), not a processing deadline. The exclusive root lock is held through cleanup. A process-wide semaphore allows two children, and waiting is cancelable without spending a child's optional elapsed budget.

## Eligibility and acquisition

Canonical single-video URLs, matching video IDs, YouTube extractor checks, no playlists, ignored yt-dlp config and shell-free ArgumentList execution remain mandatory. Positive finite numeric duration is required. Missing, malformed, zero or nonfinite duration is invalid information, not excessive length. Finite completed `was_live` archives are accepted alongside `not_live`. Active, upcoming, post-live/not-yet-finalized and unknown statuses are rejected.

Two yt-dlp match filters OR the accepted statuses; each filter ANDs finite-duration and non-live checks. Disabled caps emit no maximum-size or upper-duration filter. The positive-duration validation is not a maximum duration policy. Tests execute the real pinned yt-dlp parser. Audio acquisition/extraction remains yt-dlp's responsibility.

## Subprocess and audio contracts

The six-argument invocation constructor defaults to strict Metadata stdout. Diagnostic stdout and all stderr drain continuously with bounded tail retention; total emitted progress is not a fatal event. Metadata retains the historical decoded-character units despite the MaxMetadataBytes name, normalized to 4 Ki..1 Mi characters. Acquisition/tagging retain 64 Ki stdout/32 Ki stderr, verification 8 Ki per stream.

Cancellation, failure, stall and explicit elapsed expiry kill the child tree, close pipes and observe cleanup with a two-second teardown budget before releasing the slot. Trusted media tools are required: standard .NET tree termination is not a hostile self-detaching-process sandbox. Linux real-process tests execute; native Windows execution remains a release gate. Windows construction uses ArgumentList, CreateNoWindow and the NUL sink.

Tagging uses `-c:a copy`, preserves encoded AAC packet hashes, writes authoritative tags, attaches a replacement cover or retains the existing one. Full verification decodes through EOF using `-xerror` and bounded progress output. Successful new imports require clean diagnostics, `progress=end`, positive decoded duration, and duration within two seconds of authoritative metadata. The fixed rounding tolerance does not grow with track length. Corrupt tails and a short payload falsely advertised as hours long cannot be promoted.

Legacy indexed media has no stored expected duration, so reuse verifies full decodability and positive duration. A read or verification-tool failure is not evidence of corruption: it must not quarantine an otherwise valid existing file or its index. Managed I/O faults propagate to a safe error; staging is cleaned best-effort. If promotion succeeded but index writing failed, the error acknowledges that a file may already exist and explicit retry checks it safely. Definitively invalid container headers/index data still follow the existing quarantine logic. No already-valid media is overwritten.

## Integration and release

CI runs all four tracked projects: command, engine, delivery and regression. See their READMEs for fixture truth and prerequisites. The engine project explicitly source-links only its resource monitor, not all long-media delivery classes. The full production project is also compiled by the other suites.

Local generated-audio and synthetic boundary tests do not establish a live upstream download or deployment. Independent exact-SHA review, native Windows runtime validation and release-owner configuration/deployment verification remain separate gates.
