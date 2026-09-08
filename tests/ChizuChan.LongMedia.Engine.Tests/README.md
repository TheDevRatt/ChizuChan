# Long-media engine tests

Prerequisites: .NET 10 SDK, ffmpeg and ffprobe on PATH, Python 3 with the pinned yt-dlp package from requirements.txt. No video download, network metadata fetch or Discord call occurs during tests. yt-dlp is used only to evaluate its real match-filter parser.

Linux/WSL:

```sh
uv venv /tmp/chizu-engine-test-python
uv pip install --python /tmp/chizu-engine-test-python/bin/python -r tests/ChizuChan.LongMedia.Engine.Tests/requirements.txt
export CHIZU_TEST_PYTHON=/tmp/chizu-engine-test-python/bin/python
flock -o /tmp/chizu-long-media-dotnet.lock dotnet test tests/ChizuChan.LongMedia.Engine.Tests -c Release -p:UseSharedCompilation=false --logger 'trx;LogFileName=engine.trx'
```

`flock -o` is important: plain flock leaks its open lock descriptor into persistent compiler servers, which can deadlock parallel lanes after a build exits. Use -o and disable shared compilation. Results default to this project's ignored TestResults directory.

Windows PowerShell, with the same tools installed:

```powershell
python -m venv $env:TEMP\chizu-engine-python
& $env:TEMP\chizu-engine-python\Scripts\python.exe -m pip install -r tests/ChizuChan.LongMedia.Engine.Tests/requirements.txt
$env:CHIZU_TEST_PYTHON = "$env:TEMP\chizu-engine-python\Scripts\python.exe"
dotnet test tests/ChizuChan.LongMedia.Engine.Tests -c Release -p:UseSharedCompilation=false --logger "trx;LogFileName=engine-windows.trx"
```

CHIZU_TEST_FFMPEG and CHIZU_TEST_FFPROBE can override executable locations. The suite fails, rather than silently skipping real-media assertions, when required tools are unavailable. Native Windows commands are provided for the integration/release lane; the engine implementation lane exercised Linux/WSL only.

Coverage includes 4h, 4h+1s, 6h+1s and 7h finite metadata, optional/disabled policies, completed archives versus live/upcoming streams, malformed metadata, real yt-dlp filter syntax, multi-megabyte stdout/stderr floods, strict metadata overflow, canceled process-slot and video-lock waiters, child-tree cancellation, explicit elapsed timeout, progress-aware stalls, deterministic preflight/live/post-exit low disk, actual generated AAC and JPEG tagging with unchanged packet hashes, cover retention, duration and tail corruption, atomic promotion/reuse, lowered-policy preservation and cancellation/staging cleanup.

The acquisition adapter in AudioTests copies a real three-second generated M4A fixture and provides synthetic authoritative metadata. All encoding, tagging, verification and ffprobe packet hashing are real child processes. Long boundaries use metadata, not large network downloads or pretend audio fixtures.

The project links the actual production engine sources to remain lightweight and isolated from unrelated bot dependencies. The existing ChizuChan.YouTubeDownload.Tests project separately builds the full production project and checks command compatibility. Changes to the long-media helper must also be linked by any other source-linking suite.
