# Long-media acceptance regressions

**RED test branch, not release-ready.** Independently authored against unchanged production revision `74ea2fa5324bf49df033da5b77a08ddea0bae50f`. This suite is an acceptance contract, not an implementation review or a claim that the fix exists. No production source, existing test project, service, deployed configuration, Discord message, or upstream media was changed by this lane.

## Run locally and in CI

Prerequisites: .NET SDK 10.x, Python 3, ffmpeg with AAC/lavfi/MJPEG, ffprobe, and a local filesystem supporting sparse files and symbolic links. CI uses Ubuntu. WSL execution was verified with SDK 10.0.302, Python 3.11.15, and ffmpeg/ffprobe 6.1.1-3ubuntu5. Windows execution/native service hosting is **not** established by this Linux suite.

```bash
dotnet test tests/ChizuChan.LongMedia.Regression.Tests/ChizuChan.LongMedia.Regression.Tests.csproj \
  --configuration Release --verbosity minimal \
  --logger 'trx;LogFileName=long-media.trx' --results-directory TestResults/long-media

# Real audio only, still expected RED on baseline:
dotnet test tests/ChizuChan.LongMedia.Regression.Tests/ChizuChan.LongMedia.Regression.Tests.csproj \
  --configuration Release --verbosity minimal --filter Category=RealAudio

# Keep the existing independently tracked URL/auth/command contracts:
dotnet test tests/ChizuChan.YouTubeDownload.Tests/ChizuChan.YouTubeDownload.Tests.csproj \
  --configuration Release --verbosity minimal
```

For concurrent local lanes, prefix every dotnet build/test command with:

```bash
flock /tmp/chizu-long-media-dotnet.lock /home/devratt/.dotnet/dotnet ...
```

Tool discovery uses PATH, or explicit absolute `CHIZU_TEST_PYTHON3`, `CHIZU_TEST_FFMPEG`, and `CHIZU_TEST_FFPROBE` overrides. Missing prerequisites are errors, never skipped tests. No yt-dlp installation, cookies, application configuration, Discord token, or remote media is needed. Only temporary test directories are written and removed. Tests reference the real production project, not copied sources or production stubs. The production project already excludes `tests/**/*.cs`; no runtime identifier workaround or production project edit was needed.

`.github/workflows/security.yml` adds a separate `long-media-regressions` job and leaves the existing command suite and scanner jobs unchanged. It installs offline test tools, runs the full suite without filtering or `continue-on-error`, and uploads TRX even on failure. The 15-minute **CI safety timeout is not a production job limit**. Checkout v4.2.2, setup-dotnet v5.4.0 and upload-artifact v4.6.2 pins were checked against their release refs.

## Executable contracts and fixture truth

| Group | What is actually exercised |
|---|---|
| Metadata | Public production parser; finite 900/901, 14,400/14,401, 21,600/21,601 and 25,200 seconds; no default ceiling; explicit duration policy; missing, malformed, nonfinite duration; malformed live flags; active/upcoming/unfinalized/unbounded streams denied; finalized finite archives accepted; extractor/identity/playlist guards retained. |
| Optional budgets | Existing options/getters, argument builder and invocations captured from the real handler; 0 and -1 mean disabled, no legacy duration/filesize arguments when disabled/default, no unconditional archive-origin filter, no hidden six-hour/1-GiB/one-hour upper clamps. Explicit positive acquisition/processing budgets remain enforceable. Full-track acquisition/tagging defaults must use `Timeout.InfiniteTimeSpan`; probe and lock budgets may stay finite. |
| Sparse orchestration | Real handler, locks, paths, file-size checks, staging, index and atomic promotion with a fake external tool. Files contain only an M4A header and sparse length. **They are not audio.** Large size fixtures of 460,800,000 and 1,073,741,825 bytes test byte arithmetic, not playback or codec throughput. Separate acquired/final boundaries test limit-minus-one, limit and limit-plus-one, including output larger than input. |
| Process | Real short Python children run through `YouTubeDownloadTool`; stage policy is captured from actual acquisition/tagging invocations then retained using record `with` substitution. Exact capture bound and bound-plus-one, individual pipes and simultaneous pipe saturation, bounded nonempty retained diagnostics and successful producer completion. Strict metadata overflow uses a valid JSON prefix followed by whitespace so truncation cannot masquerade as success. User cancellation, parent/descendant termination, explicit timeout, slot reuse and pre-cancel no-start are verified. WSL zombies awaiting init reaping count as terminated, not executing. |
| Real audio | Locally generate a 2.5-second AAC sine tone and JPEG, substitute only acquisition/metadata, execute real production tagging and verification. Independently ffprobe the output, decode the **entire** audio to PCM, check duration and audible tail, authoritative tags and cover. Negative duration-integrity fixture has 180s metadata but 2.5s real payload. Existing-media regression appends a real ISO-BMFF `free` box, fully decodes that file, lowers the acquisition policy, and checks byte identity/no quarantine/no reacquisition. No sparse file is supplied to a real decoder. |
| Safety preservation | Canonical single-video URL, `--ignore-config`, `--no-playlist`, shell-disabled ArgumentList invocation, invalid ID/root rejection, symlink boundary, no publication on tag failure, no published file before tagging completes, index reuse and no overwrite. Existing command-suite auth/URL cases still run separately in CI. |

A healthy short process with an explicit infinite timeout is exercised. Absence of the old 300-second default stage deadline is checked on the handler's real invocation policy. This does **not** simulate hours of wall time, establish a progress-aware watchdog, or prove throughput for a seven-hour encoding.

## Integration guidance

1. Cherry-pick this test/CI commit into a dedicated integrated candidate alongside the engine and durable-completion commits. Do not merge this RED-only branch into master or treat it as deployable.
2. Run both tracked test projects and production build. Preserve the baseline RED evidence in `BASELINE-RED.md`; capture a new candidate TRX rather than relabeling the old run GREEN.
3. Keep the public behavior assertions. Existing public constructors, records and argument builders were preferred because candidate APIs did not exist. If a candidate adds dependencies or changes a real verification stage, adapt only fixture wiring/explicit stage dispatch to those actual APIs, and record the adaptation. Do not weaken assertions, silently ignore unknown tool invocations, fabricate production stubs, or copy candidate logic into tests.
4. `SyntheticAcquisitionTool` deliberately rejects unknown stages. A new ffprobe duration API will need an explicit **synthetic** metadata response for sparse fixtures. Keep real-audio tests on real ffprobe/ffmpeg and continue measuring with independent fixture tooling. Do not pretend the sparse fixtures satisfy a decoder.
5. Process tests preserve invocation record fields from the handler, including any added stage capture policy. If metadata and diagnostic output become separate APIs, wire tests to the real stage contracts; metadata must still fail on overflow while ordinary diagnostics drain successfully.
6. Existing synchronous command tests do not establish durable job completion. The following candidate-dependent acceptance items are intentionally documented rather than represented by empty, skipped or invented-API tests. An integrated candidate is **not fully accepted** until these are implemented and exercised.

## Candidate-dependent acceptance still required

### Low disk and explicit resource budgets

Baseline has no injectable free-space/quota seam. Do not fill the developer/CI volume or create a fake production disk API solely to make a test compile. Once the candidate's actual disk policy exists, exercise it through its public job/import seam with a controllable free-space source:

- Known free space below the configured floor: deny before acquisition with a resource-specific safe result, no process spawned, no promotion/index, and no mutation of valid indexed media.
- Exactly the floor and just above it: define reservation semantics explicitly. Free space must cover reservations **and** the floor, including simultaneous staging input/output/cover/faststart work, not just final file length.
- Begin above the floor, then fall below it while writing: cancel/kill the process tree, drain bounded logs, clean staging and release locks/reservations. A later healthy job must succeed.
- Unknown remote filesize must not bypass measured local disk/staging checks. A disabled per-file budget must not disable disk safety. Failed/free-space-unavailable queries need explicit fail-safe behavior, not silent unlimited acceptance.
- Positive separate input/output/staging budgets, if exposed, must enforce independently at each boundary. No default finite-duration or fixed-size proxy may replace genuine storage checks. Existing valid library media must never be quarantined merely for exceeding a newly lowered acquisition policy.
- Add crash/orphan cleanup and concurrency/reservation recovery against real candidate persistence, without restarting services or touching a live library.

### Expired Discord interaction and durable completion

Baseline has no durable YouTube job-store/completion-transport acceptance seam. Do not assert guessed class names, constructor signatures, NetCord internals, or a fake job implementation. Once the command lane supplies the actual seam, use its real coordinator/store and fake only clock/Discord transport:

- Authorized direct-link and music-search-button requests acknowledge/queue promptly with a stable job ID; denial never queues or invokes the downloader. Assert actual production command wiring, not just a test-only coordinator.
- Advance a fake clock beyond 15 minutes, make every interaction-token edit fail as expired, then complete the job. The imported file and persisted terminal success survive; completion/status uses bot-authenticated delivery independent of the expired token.
- User ID and job ID are preserved. Delivery goes only to the authorized requester. No credentials, raw tool arguments, private filesystem paths, cookies, or unsafe mentions leak into status/notifications.
- Restart/recreate the worker/store in the fixture. Pending/running/terminal records recover according to the specified policy; a completed import is not repeated and a notification failure is not converted into a download failure.
- Retry failed notifications with stable idempotency/delivery identity. Confirm no duplicate import or duplicate terminal success message and visible status when delivery remains unavailable.
- Interaction expiration is not job cancellation. User cancellation and host shutdown must propagate through durable jobs to tool-tree cleanup, persist the correct state, and avoid a success notification for a canceled import.
- Cancellation while queued or waiting for process/root locks, bounded admission/concurrency, and any progress/stall watchdog need real candidate APIs plus fake time. Do not add real 15-minute or multi-hour sleeps to CI.

No live Discord send, upstream download, deployment or final integrated review is part of this suite's baseline evidence.
