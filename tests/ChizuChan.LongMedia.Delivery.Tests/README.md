# Durable YouTube delivery

This project tests the real JSON journal, hosted worker, production registration helper, direct command coordinator, music-search coordinator/session claims, production-context module discovery, and mention-free message construction. Only acquisition and the Discord network boundary are faked. No YouTube media or Discord messages are sent by these tests.

## Run

```sh
flock /tmp/chizu-long-media-dotnet.lock /home/devratt/.dotnet/dotnet test \
  tests/ChizuChan.LongMedia.Delivery.Tests/ChizuChan.LongMedia.Delivery.Tests.csproj \
  --configuration Release --verbosity minimal
```

CI integration must add this project alongside the existing command and engine/regression suites. This delivery lane intentionally does not edit CI or the existing tracked command-test project.

## Production wiring

`Program.AddYouTubeLongMediaDelivery` is called after assembly scanning. It captures the acquisition handler registration and replaces the public `IYouTubeMusicActionHandler` with one singleton `YouTubeLongMediaDelivery`, also registered as an `IHostedService`. The original handler is constructed inside the adapter without changing its interface. Both `/youtube_download` and the YouTube branch of `MusicSearchActionCoordinator` therefore perform short durable admission instead of awaiting acquisition. Existing constructors, download permission/rate-limit checks, source-message/token/index ownership checks, and non-YouTube code are unchanged.

The existing command/component deferral and response edit now acknowledge admission only. No interaction token, callback, Discord-supplied title, URL, or source channel is stored in the job. The separate `YouTubeLongMediaMessenger` opens the stored owner's DM through the normal authenticated bot REST client and sends with `AllowedMentionsProperties.None` and a persisted unique nonce. The Arr notification DTO/store are deliberately not reused: YouTube acquisition success is not necessarily positive Plex readiness. The engine's bounded result message is preserved rather than inventing library readiness.

`/youtube_job [id]` is a DM-only, owner-filtered status command. Omit the ID for the newest retained job, including when the original acknowledgment was lost. Unknown IDs and IDs belonging to another user produce the same response.

## State and recovery policy

- Admission is serialized and persisted with a flushed temporary snapshot and atomic replacement before returning an ID. A lifetime file lease rejects overlapping hosts using the same local journal.
- Queued jobs resume automatically. A fixed worker pool processes jobs with the hosted-service cancellation token, never the interaction lifetime. There is no media-duration, media-size, or total-execution deadline here.
- A graceful shutdown cancels acquisition and persists `Interrupted`. An on-disk `Running` record at startup also becomes `Interrupted`. It is not automatically replayed: promotion might have completed before the crash. The DM/status tells the owner it may already have imported and to check Plex before resubmitting. A deliberate retry goes back through the engine's existing indexed-file/idempotency checks.
- Same-owner/same-video queued, running, or successful jobs reuse the retained job ID, including across direct/search entry points and restart. Failed/interrupted jobs allow a new attempt. Different users never share a user-visible job identity. Successful retained jobs are historical completion records, not fresh filesystem validation. To deliberately reimport a file removed outside the bot, an operator must archive that successful job record while the service is stopped.
- Completion state is persisted before delivery. DM failure does not turn a successful import into a failed download. Delivery error/retry state is persisted and visible via `/youtube_job`; retries resume after restart without redownloading. Successful delivery records the actual Discord message ID. A duplicate request after delivery says the DM was already delivered, rather than promising another DM.
- Delivery is at least once across a crash between Discord accepting a send and persisting the returned ID. Reusing Discord's unique nonce reduces duplicates within Discord's nonce window, but does not establish unlimited exactly-once delivery. The job ID remains stable in any repeated notification.
- Journal errors during work fault the hosted service and cancel/observe sibling tasks. They are not swallowed into permanently running jobs. The standard host failure policy stops the host; startup recovery requires writable, valid storage. Corrupt/oversized journals fail closed and are not erased.

## Release configuration and operations

Add the optional `YouTubeLongMediaDelivery` section to the integrated example and allowlisted deployment configuration. Defaults work without this section, but the journal location and ACL must be verified before promotion:

| Setting | Default | Purpose |
|---|---|---|
| `StorePath` | Windows: `%ProgramData%/ChizuChan/youtube-long-media-jobs.json`; other OS: local application data `ChizuChan/youtube-long-media-jobs.json` | Fully qualified, stable, private local journal outside versioned deployment directories. |
| `MaxPendingJobs` | 16 | Bounded queued plus running admission. |
| `Concurrency` | 1 | Fixed acquisition workers; must be positive and no greater than `MaxPendingJobs`. Default avoids contention on the existing exclusive library root. |
| `MaxStoredJobs` | 1000 | Total retained history plus active jobs. Must be at least `MaxPendingJobs`. |
| `MaxStoreBytes` | 16777216 | Journal storage budget, with a conservative 4096-byte terminal/delivery record reservation per accepted job. Snapshot replacement temporarily requires space for both old and new journal. This is not a media-file cap. |
| `PollIntervalMilliseconds` | 1000 | Queue/delivery polling interval, positive. |
| `DeliveryRetrySeconds` | 30 | Retry spacing for failed result DMs, positive. No maximum download execution time is introduced. |

The service account needs read/write/create/replace access to the journal parent directory and `.lock`/`.tmp` siblings. Protect the directory and backups: records contain Discord user IDs and video IDs. Use a single local filesystem journal per bot deployment and keep its path unchanged across version promotions/rollback. Do not store it inside the exclusive media root, and do not copy or publish it as code. A downgrade may stop processing these jobs; retain the journal for a later compatible release.

History is never silently discarded. When retained records or reserved journal capacity are full, new work is rejected with an explicit storage error; active jobs and status remain intact. An operator can stop the service, back up the journal, and archive selected terminal records whose DMs were delivered, then restart. Never remove queued/running or undelivered terminal records merely to free space. Do not lower journal limits below existing state requirements.

No runtime configuration, credentials, profiles, service, firewall, canonical checkout, or other lane-owned file is changed by this project. Integrate with the engine lane to remove acquisition-layer limits. Integration still needs the combined CI run, independent review, and Windows release/deployment verification; unit tests are not evidence of a real upstream long-media download.
