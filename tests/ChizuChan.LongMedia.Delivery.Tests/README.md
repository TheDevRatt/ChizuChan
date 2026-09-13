# Durable YouTube delivery

This suite executes the JSON journal, hosted worker, production DI helper, command/search coordinators, ownership claims, module discovery, and mention-free message construction. Acquisition and the Discord network boundary are substituted; no upstream media or Discord messages are sent.

```sh
flock -o /tmp/chizu-long-media-dotnet.lock dotnet test tests/ChizuChan.LongMedia.Delivery.Tests/ChizuChan.LongMedia.Delivery.Tests.csproj -c Release -p:UseSharedCompilation=false --logger 'trx;LogFileName=delivery.trx'
```

CI includes this suite with engine, independent regression and existing command tests.

## Wiring and lifecycle

`Program.AddYouTubeLongMediaDelivery` runs after assembly scanning, captures the original engine registration, and installs a singleton hosted delivery adapter at the unchanged action interface. Direct `/youtube_download` and YouTube music-search actions therefore acknowledge durable admission, not completion. Existing permissions, rate limits, source-message/session/index ownership and non-YouTube actions are unchanged.

No interaction token, callback, source channel or Discord-supplied media metadata is journaled. Bot-authenticated completion opens the owner's DM, sends with AllowedMentions.None and a stable unique nonce. `/youtube_job [id]` is DM-only and owner-filtered, usable after interaction expiry or restart. A missing ID queries the latest retained job.

- Admission is serialized, flushed and atomically persisted before returning a job ID. A lifetime local file lease prevents overlapping hosts.
- Queued/running same-owner/same-video requests share a job ID, including across command entry points. Different owners never share a visible identity.
- Terminal records never suppress an explicit new attempt. The engine checks its index under the media lock: intact files are reused without reacquisition, externally deleted files can be reimported without manual JSON edits.
- Queued work resumes after restart. Running work becomes Interrupted rather than replaying blindly after an uncertain promotion. Graceful shutdown propagates cancellation and persists Interrupted. Explicit retry safely re-enters the engine.
- Completion is durable before DM delivery. Failed notifications retry across restart without redownloading or changing a successful import into failure. Delivered records are not automatically resent.
- Delivery is at least once across the gap between Discord accepting a send and persisting its ID. Stable nonces reduce duplicates within Discord's nonce window, not indefinitely. Status remains queryable if DMs stay unavailable.
- Real journal-write failures prevent admission acknowledgment or fault the worker host and cancel siblings. Corrupt JSON fails closed without erasing the journal. A successful media promotion may outlive a journal write failure.

## Options and history

`YouTubeLongMediaDelivery` defaults:

| Setting | Default | Meaning |
|---|---|---|
| StorePath | Windows common application data, otherwise local application data, under ChizuChan/youtube-long-media-jobs.json | Stable private local journal, outside deployment versions and the exclusive media root. |
| MaxPendingJobs | 16 | Bounds queued plus running work only. |
| Concurrency | 1 | Positive worker count, no greater than MaxPendingJobs. |
| MaxStoredJobs | 1000 | Recent delivered terminal records retained, not total records or admission capacity. Zero retains none after delivery. |
| PollIntervalMilliseconds | 1000 | Positive queue/delivery polling interval. |
| DeliveryRetrySeconds | 30 | Positive failed-DM retry spacing. |

Delivered terminal history rotates automatically. Queued/running jobs and terminal results still awaiting delivery are never discarded for retention. Undeliverable results can accumulate until delivery succeeds; journal memory and snapshot I/O consequently grow with those records. There is no journal byte budget, reserve or aggregate disk-admission cap. The removed MaxStoreBytes configuration field is ignored and should be removed from deployment configuration if present. Atomic replacement temporarily requires old and new snapshots; actual filesystem errors are handled truthfully.

The service account needs create/read/write/replace access to the journal parent and lock/temp siblings. Protect the directory and backups because jobs contain owner/video IDs. Keep the path stable across release/rollback; never copy live journals, cookies or secrets into code artifacts.

The independent regression suite additionally runs a real-audio engine through this durable adapter and verifies explicit reuse then reimport after external deletion. Local tests are not native Windows, upstream-download, Discord-connectivity or deployment evidence.
