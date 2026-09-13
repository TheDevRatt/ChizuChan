# Integrated long-media acceptance regressions

Originally authored RED against `74ea2fa5324bf49df033da5b77a08ddea0bae50f`; historical evidence remains in BASELINE-RED.md. The integration adapts synthetic verification responses to the engine's actual progress protocol. It does not replace real decoding with fake audio evidence.

## Running

Requires .NET 10, Python 3, ffmpeg with AAC/lavfi/MJPEG, ffprobe and a filesystem supporting sparse files and symbolic links. Missing tools fail tests, never silently skip them. Use PATH or absolute CHIZU_TEST_PYTHON3, CHIZU_TEST_FFMPEG and CHIZU_TEST_FFPROBE overrides. The engine suite separately requires its pinned yt-dlp package, installed by CI.

```sh
flock -o /tmp/chizu-long-media-dotnet.lock dotnet test tests/ChizuChan.LongMedia.Regression.Tests/ChizuChan.LongMedia.Regression.Tests.csproj -c Release -p:UseSharedCompilation=false --logger 'trx;LogFileName=regression.trx'
```

CI runs all four tracked projects without filtering or continue-on-error, retaining each long-media TRX. CI's bounded job timeout is infrastructure safety, not a production download limit. Native Windows process/audio behavior must still be exercised by the release owner.

## Acceptance and fixture truth

| Area | Evidence |
|---|---|
| Metadata | Public parser: finite 900/901, 14400/14401, 21600/21601 and 25200 seconds; explicit optional caps without hidden clamps; malformed/missing/nonfinite durations rejected accurately; completed archives allowed; active/upcoming/unfinalized streams, wrong identity/extractor and playlists rejected. |
| Optional caps | Zero/negative disables duration, file size and elapsed limits. Defaults emit no upper-duration/max-filesize gate. Positive-duration validation is retained to reject unbounded media. Disabled invocation deadlines may be zero or InfiniteTimeSpan; real process tests prove behavior, not a sentinel spelling. |
| Sparse orchestration | Real handler/locks/path checks/index/atomic move, synthetic acquisition and verification. M4A headers with sparse lengths of 460800000 and 1073741825 bytes are NOT audio. Verification emits synthetic progress matching metadata only within this explicit fake-tool adapter. Unknown tool stages still fail. |
| Real processes | Short Python children, handler-captured stage policies, both pipe floods, exact/bound-plus-one retention, strict metadata overflow, cancellation and descendant cleanup, slot release. Healthy default acquisition and tagging complete beyond an explicit deadline that kills the identical healthy fixture when enabled. |
| Real audio | Generated 2.5-second AAC tone and JPEG; synthetic network acquisition only. Real production ffmpeg tag/verify and independent ffprobe/full PCM decoding measure duration, audible tail, cover and authoritative tags. A 2.5-second payload with 180-second metadata cannot import. Engine tests additionally compare encoded packet hashes and detect damaged tails. |
| Existing files | Lowered acquisition limits never quarantine valid real media. Verification I/O exceptions, nonzero verifier exits and real exclusive file/index locks must preserve original bytes/index with truthful failure. |
| Durable integration | Real hosted adapter/store and real-audio engine, substituted network messenger. Explicit retry creates a new job but reuses existing media without reacquisition; deletion outside the application permits a subsequent successful reimport. |
| Security | Canonical URL/no playlists/ignore-config/no shell; root/ID/symlink boundaries; clean staging and no index/publication on failed tags; nonoverwriting import and indexed reuse. The unchanged command suite separately covers URL/auth contracts. |

## Superseded requirements

The user's explicit correction removes all application free-space floors, disk reserves, staging quotas and aggregate storage-admission gates. Earlier low-disk denial specifications were invalid and are not acceptance requirements. Real filesystem failure handling and preserving already-valid media remain required. Bounded diagnostic capture, concurrency, cancellation and configurable stall detection remain valid safeguards.

Terminal history cannot eventually block admission. Delivery tests cover automatic rotation of delivered history, preserving pending notifications, restart recovery, shutdown, owner isolation, interaction expiry and bot-authenticated retry. The new integration test ties journal retry to actual audio imports rather than a fake successful engine result.

No multi-hour wall-clock run, actual YouTube download, real Discord send, Windows service operation or deployment is claimed. Synthetic long boundaries and local generated-media results are explicitly different evidence classes.
