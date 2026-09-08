# Verified baseline RED evidence

**Not release-ready. No integrated candidate was reviewed.** Production is unchanged at `74ea2fa5324bf49df033da5b77a08ddea0bae50f`; branch `test/youtube-long-media-regressions` owns only this test project/documentation and the new CI job.

## Actual runs

All commands ran from `/home/devratt/chizu-long-media-fix/regressions`, with `/home/devratt/.dotnet/dotnet` and `flock /tmp/chizu-long-media-dotnet.lock`. No production configuration was loaded and no upstream media downloaded.

| Run | Passed | Failed | Skipped | Exit |
|---|---:|---:|---:|---:|
| Existing tracked command suite before writes | 69 | 0 | 0 | 0 |
| First focused default-duration RED | 1 | 6 | 0 | 1 |
| Final independently authored acceptance suite | 51 | 45 | 0 | 1 |
| Immediate repeated final acceptance suite | 51 | 45 | 0 | 1 |
| Separate real-audio-only run | 1 | 2 | 0 | 1 |
| Existing tracked command suite after adding regressions | 69 | 0 | 0 | 0 |

Final and repeated runs discovered 96 identical cases with identical outcomes, zero test infrastructure errors/timeouts/aborts/not-runnable cases, and no new build/analyzer warnings. The first focused run had one xUnit analyzer suggestion in the new helper, corrected before final verification. Initial production build emitted existing nullable/platform warnings; the separate incremental production Release build subsequently returned `Build succeeded. 0 Warning(s), 0 Error(s)`. Production evaluated compilation contained 101 files and **zero test sources**.

Exact principal command:

```bash
flock /tmp/chizu-long-media-dotnet.lock /home/devratt/.dotnet/dotnet test   tests/ChizuChan.LongMedia.Regression.Tests/ChizuChan.LongMedia.Regression.Tests.csproj   --configuration Release --verbosity minimal   --logger 'trx;LogFileName=baseline-acceptance-red.trx'   --results-directory /home/devratt/chizu-long-media-fix/regression-evidence
```

The repeat command changes only the TRX filename to `repeat-acceptance-red.trx`. The real-audio command adds `--filter Category=RealAudio`. The focused first RED adds `--filter FullyQualifiedName~Default_policy_accepts_finite_media_without_duration_ceiling`. Existing baseline/recheck use `tests/ChizuChan.YouTubeDownload.Tests/ChizuChan.YouTubeDownload.Tests.csproj` with the same Release/minimal/TRX options. Production checks: `dotnet build ChizuChan.csproj -c Release --verbosity minimal` and `dotnet msbuild ChizuChan.csproj -getItem:Compile`, also under flock.

Environment: WSL Linux `6.6.87.2-microsoft-standard-WSL2`, SDK `10.0.302`, Python `3.11.15`, ffmpeg/ffprobe `6.1.1-3ubuntu5`. No runtime override was required to reference the win-x64 production project's managed assembly from Linux net10 tests. This does not verify Windows native execution or a deployed service.

## Exact output summaries

```text
tracked-baseline: Passed!  - Failed:     0, Passed:    69, Skipped:     0, Total:    69, Duration: 112 ms - ChizuChan.YouTubeDownload.Tests.dll (net10.0)
focused-red: Failed!  - Failed:     6, Passed:     1, Skipped:     0, Total:     7, Duration: 16 ms - ChizuChan.LongMedia.Regression.Tests.dll (net10.0)
baseline-acceptance-red: Failed!  - Failed:    45, Passed:    51, Skipped:     0, Total:    96, Duration: 3 s - ChizuChan.LongMedia.Regression.Tests.dll (net10.0)
repeat-acceptance-red: Failed!  - Failed:    45, Passed:    51, Skipped:     0, Total:    96, Duration: 3 s - ChizuChan.LongMedia.Regression.Tests.dll (net10.0)
real-audio-red: Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3, Duration: 1 s - ChizuChan.LongMedia.Regression.Tests.dll (net10.0)
tracked-after-regressions: Passed!  - Failed:     0, Passed:    69, Skipped:     0, Total:    69, Duration: 115 ms - ChizuChan.YouTubeDownload.Tests.dll (net10.0)
```

Real audio output captured in both final TRX runs:

```text
Real generated source: 2.5s; ffprobe tagged duration: 2.507007s; decoded PCM: 40124 bytes; tail RMS: 2850.17; cover and authoritative tags verified.
```

## Every baseline failure

These are product-contract failures, not compiler/setup errors. Test identifiers and messages below are extracted from the final TRX. Passing safety/characterization cases remain enabled; no tests are skipped to conceal RED.

### `MetadataAcceptanceTests.Default_policy_accepts_finite_media_without_duration_ceiling(seconds: 14400)`

```text
Finite 14400s media rejected by default: That track is too long to download.
```

### `MetadataAcceptanceTests.Default_policy_accepts_finite_media_without_duration_ceiling(seconds: 14401)`

```text
Finite 14401s media rejected by default: That track is too long to download.
```

### `MetadataAcceptanceTests.Default_policy_accepts_finite_media_without_duration_ceiling(seconds: 21600)`

```text
Finite 21600s media rejected by default: That track is too long to download.
```

### `MetadataAcceptanceTests.Default_policy_accepts_finite_media_without_duration_ceiling(seconds: 21601)`

```text
Finite 21601s media rejected by default: That track is too long to download.
```

### `MetadataAcceptanceTests.Default_policy_accepts_finite_media_without_duration_ceiling(seconds: 25200)`

```text
Finite 25200s media rejected by default: That track is too long to download.
```

### `MetadataAcceptanceTests.Default_policy_accepts_finite_media_without_duration_ceiling(seconds: 901)`

```text
Finite 901s media rejected by default: That track is too long to download.
```

### `MetadataAcceptanceTests.Explicit_duration_budget_above_six_hours_has_no_hidden_clamp(budget: 21601)`

```text
That track is too long to download.
```

### `MetadataAcceptanceTests.Explicit_duration_budget_above_six_hours_has_no_hidden_clamp(budget: 25200)`

```text
That track is too long to download.
```

### `MetadataAcceptanceTests.Finalized_finite_archive_is_accepted(seconds: 25200)`

```text
Live streams and premieres can't be downloaded.
```

### `MetadataAcceptanceTests.Finalized_finite_archive_is_accepted(seconds: 30)`

```text
Live streams and premieres can't be downloaded.
```

### `MetadataAcceptanceTests.Malformed_duration_is_not_reported_as_a_long_track`

```text
Assert.DoesNotContain() Failure: Sub-string found
                       ↓ (pos 14)
String: "That track is too long to download."
Found:  "too long"
```

### `MetadataAcceptanceTests.Malformed_live_flags_are_rejected_instead_of_silently_treated_as_false(field: "is_live", value: "1")`

```text
Malformed is_live was accepted: 1
```

### `MetadataAcceptanceTests.Malformed_live_flags_are_rejected_instead_of_silently_treated_as_false(field: "is_live", value: "\"true\"")`

```text
Malformed is_live was accepted: "true"
```

### `MetadataAcceptanceTests.Malformed_live_flags_are_rejected_instead_of_silently_treated_as_false(field: "was_live", value: "[]")`

```text
Malformed was_live was accepted: []
```

### `MetadataAcceptanceTests.Malformed_live_flags_are_rejected_instead_of_silently_treated_as_false(field: "was_live", value: "{}")`

```text
Malformed was_live was accepted: {}
```

### `MetadataAcceptanceTests.Nonpositive_configured_duration_disables_legacy_gate(disabled: -1)`

```text
That track is too long to download.
```

### `MetadataAcceptanceTests.Nonpositive_configured_duration_disables_legacy_gate(disabled: 0)`

```text
That track is too long to download.
```

### `OptionalBudgetAcceptanceTests.Archive_provenance_is_not_an_unconditional_download_filter`

```text
Assert.DoesNotContain() Failure: Filter matched in collection
                                                     ↓ (pos 3)
Collection: [···, "--no-playlist", "--match-filter", "!is_live & !was_live & duration <= 0", "--max-filesize", "0", ···]
```

### `OptionalBudgetAcceptanceTests.Default_acquisition_arguments_do_not_reintroduce_a_duration_gate`

```text
Assert.DoesNotContain() Failure: Filter matched in collection
                                                     ↓ (pos 3)
Collection: [···, "--no-playlist", "--match-filter", "!is_live & !was_live & duration <= 900", "--max-filesize", "104857600", ···]
```

### `OptionalBudgetAcceptanceTests.Default_acquisition_arguments_do_not_reintroduce_a_size_gate`

```text
Assert.DoesNotContain() Failure: Item found in collection
                                                                              ↓ (pos 4)
Collection: [···, "--match-filter", "!is_live & !was_live & duration <= 900", "--max-filesize", "104857600", "--extract-audio", ···]
Found:      "--max-filesize"
```

### `OptionalBudgetAcceptanceTests.Default_full_track_stages_have_no_arbitrary_processing_deadline`

```text
Healthy full-track stage still has an unconditional 300s deadline. Probe/lock budgets may remain finite.
```

### `OptionalBudgetAcceptanceTests.Disabled_budgets_do_not_emit_legacy_duration_or_filesize_arguments(disabled: -1)`

```text
Assert.DoesNotContain() Failure: Filter matched in collection
                                                     ↓ (pos 3)
Collection: [···, "--no-playlist", "--match-filter", "!is_live & !was_live & duration <= -1", "--max-filesize", "-1", ···]
```

### `OptionalBudgetAcceptanceTests.Disabled_budgets_do_not_emit_legacy_duration_or_filesize_arguments(disabled: 0)`

```text
Assert.DoesNotContain() Failure: Filter matched in collection
                                                     ↓ (pos 3)
Collection: [···, "--no-playlist", "--match-filter", "!is_live & !was_live & duration <= 0", "--max-filesize", "0", ···]
```

### `OptionalBudgetAcceptanceTests.Disabled_size_budget_does_not_emit_max_filesize_argument(disabled: -1)`

```text
Assert.DoesNotContain() Failure: Item found in collection
                                                                            ↓ (pos 4)
Collection: [···, "--match-filter", "!is_live & !was_live & duration <= 0", "--max-filesize", "-1", "--extract-audio", ···]
Found:      "--max-filesize"
```

### `OptionalBudgetAcceptanceTests.Disabled_size_budget_does_not_emit_max_filesize_argument(disabled: 0)`

```text
Assert.DoesNotContain() Failure: Item found in collection
                                                                            ↓ (pos 4)
Collection: [···, "--match-filter", "!is_live & !was_live & duration <= 0", "--max-filesize", "0", "--extract-audio", ···]
Found:      "--max-filesize"
```

### `OptionalBudgetAcceptanceTests.Explicit_large_size_budget_is_not_clamped_to_one_GiB`

```text
Assert.Equal() Failure: Values differ
Expected: 2147483648
Actual:   1073741824
```

### `OptionalBudgetAcceptanceTests.Explicit_processing_budget_is_not_clamped_to_one_hour`

```text
Assert.Equal() Failure: Values differ
Expected: 7200
Actual:   3600
```

### `OptionalBudgetAcceptanceTests.Nonpositive_processing_budget_means_disabled_not_ten_second_clamp(disabled: -1)`

```text
Assert.Equal() Failure: Values differ
Expected: -00:00:00.0010000
Actual:   00:00:10
```

### `OptionalBudgetAcceptanceTests.Nonpositive_processing_budget_means_disabled_not_ten_second_clamp(disabled: 0)`

```text
Assert.Equal() Failure: Values differ
Expected: -00:00:00.0010000
Actual:   00:00:10
```

### `ProcessAcceptanceTests.Full_track_progress_finishes_while_retained_diagnostics_stay_bounded(stage: "download", stream: "both", count: 131072)`

```text
System.IO.InvalidDataException : yt-dlp output exceeded the allowed size.
```

### `ProcessAcceptanceTests.Full_track_progress_finishes_while_retained_diagnostics_stay_bounded(stage: "download", stream: "stderr", count: 1025)`

```text
System.IO.InvalidDataException : yt-dlp output exceeded the allowed size.
```

### `ProcessAcceptanceTests.Full_track_progress_finishes_while_retained_diagnostics_stay_bounded(stage: "download", stream: "stdout", count: 1025)`

```text
System.IO.InvalidDataException : yt-dlp output exceeded the allowed size.
```

### `ProcessAcceptanceTests.Full_track_progress_finishes_while_retained_diagnostics_stay_bounded(stage: "tag", stream: "both", count: 131072)`

```text
System.IO.InvalidDataException : yt-dlp output exceeded the allowed size.
```

### `ProcessAcceptanceTests.Full_track_progress_finishes_while_retained_diagnostics_stay_bounded(stage: "tag", stream: "stderr", count: 1025)`

```text
System.IO.InvalidDataException : yt-dlp output exceeded the allowed size.
```

### `ProcessAcceptanceTests.Full_track_progress_finishes_while_retained_diagnostics_stay_bounded(stage: "tag", stream: "stdout", count: 1025)`

```text
System.IO.InvalidDataException : yt-dlp output exceeded the allowed size.
```

### `RealAudioAcceptanceTests.Lower_acquisition_size_budget_does_not_quarantine_valid_existing_library_audio`

```text
Valid existing real audio was moved to quarantine solely because the acquisition size budget was lowered.
```

### `RealAudioAcceptanceTests.Successful_tool_exit_does_not_allow_grossly_truncated_real_audio_to_be_promoted`

```text
A 2.5s real audio payload was published for 180s metadata despite successful tool exits.
```

### `SparsePipelineAcceptanceTests.Default_size_policy_accepts_sparse_size_fixture_without_hidden_ceiling(length: 1073741825)`

```text
Sparse size-policy fixture of 1073741825 bytes was rejected: Couldn't download that YouTube track right now.
```

### `SparsePipelineAcceptanceTests.Default_size_policy_accepts_sparse_size_fixture_without_hidden_ceiling(length: 460800000)`

```text
Sparse size-policy fixture of 460800000 bytes was rejected: Couldn't download that YouTube track right now.
```

### `SparsePipelineAcceptanceTests.Disabled_size_policy_does_not_reject_sparse_size_fixture(disabled: -1)`

```text
Couldn't download that YouTube track right now.
```

### `SparsePipelineAcceptanceTests.Disabled_size_policy_does_not_reject_sparse_size_fixture(disabled: 0)`

```text
Couldn't download that YouTube track right now.
```

### `SparsePipelineAcceptanceTests.Finite_long_metadata_reaches_atomic_promotion_at_default_budgets(duration: 14400)`

```text
14400s import failed: That track is too long to download.
```

### `SparsePipelineAcceptanceTests.Finite_long_metadata_reaches_atomic_promotion_at_default_budgets(duration: 14401)`

```text
14401s import failed: That track is too long to download.
```

### `SparsePipelineAcceptanceTests.Finite_long_metadata_reaches_atomic_promotion_at_default_budgets(duration: 21601)`

```text
21601s import failed: That track is too long to download.
```

### `SparsePipelineAcceptanceTests.Finite_long_metadata_reaches_atomic_promotion_at_default_budgets(duration: 25200)`

```text
25200s import failed: That track is too long to download.
```

## Evidence files

Raw TRX/logs are local artifacts under `/home/devratt/chizu-long-media-fix/regression-evidence/`, not source fixtures and not fabricated sample output. CI separately retains its own TRX artifact. Intermediate development runs are retained but superseded by the final/repeat files.

| File | SHA-256 |
|---|---|
| `tracked-baseline.trx` | `b11af7636051f14b9de775ac25205dba8ddbd526826e8e1c1b166863250a2887` |
| `focused-red.trx` | `08e8837b97f30fa0968c25ae3a07c7dd40abc5d970404da7e94591c873220e47` |
| `baseline-acceptance-red.trx` | `d7b18fee1241d65fdfc12b1b2aed2485442e5a2d45241f5f028047ab4eafdd9e` |
| `repeat-acceptance-red.trx` | `638abaa6988d1b84a7fa94ab21362d9f960d4a28bd169d2ff00f4ac2e4f43dff` |
| `real-audio-red.trx` | `ad3f028347ecc978a0d6a8317f15f087b44a172f9acc49add85b6e8015013c46` |
| `tracked-after-regressions.trx` | `8216daa2d0ff77f551b60364b51d9bfaf96bf550403e725b898d9dc4a40ff4ed` |
| `production-build.log` | `8d8d7caa32f808b299e99325e36f230f223426cd2e9891a3e1ad2a3fbdd94dd1` |
| `production-compile-items.json` | `af26be7ffded9036868ba2341187be797514b4854b84371a0c8fb0d7d305f8c1` |

See README.md for fixture limitations, the actual CI command, candidate API adaptation guidance, and still-unimplemented low-disk/durable Discord acceptance. Do not label this baseline RED branch an integrated pass.
