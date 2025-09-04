using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Services.Media
{
    /// <summary>
    /// Pipes bestaudio from yt-dlp into ffmpeg and returns a PCM s16le/48k/stereo stream on stdout.
    /// The returned stream owns both processes and can terminate them via KillProcesses() or Dispose.
    /// </summary>
    public static class YtDlpFfmpeg
    {
        public static async Task<Stream> OpenPcmFromUrlAsync(string url, CancellationToken ct)
        {
            string yt = ResolveExeOrThrow("yt-dlp.exe", "yt-dlp", "yt-dlp.exe");
            string ff = ResolveExeOrThrow("ffmpeg.exe", "ffmpeg", "ffmpeg.exe");

            var pYt = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = yt,
                    Arguments = $"-o - -f ba --no-playlist \"{url}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false, // keep quiet; avoids stderr races on teardown
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                },
                EnableRaisingEvents = true,
            };

            var pFf = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ff,
                    Arguments = "-hide_banner -loglevel error -i pipe:0 -f s16le -ar 48000 -ac 2 pipe:1",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false, // keep quiet
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                },
                EnableRaisingEvents = true,
            };

            try
            {
                if (!pYt.Start()) throw new InvalidOperationException("Failed to start yt-dlp.");
                if (!pFf.Start())
                {
                    try { if (!pYt.HasExited) pYt.Kill(entireProcessTree: true); } catch { }
                    throw new InvalidOperationException("Failed to start ffmpeg.");
                }
                Console.Error.WriteLine($"[yt-dlp] pid={pYt.Id}");
                Console.Error.WriteLine($"[ffmpeg] pid={pFf.Id} (stdin<-yt-dlp, stdout->PCM)");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new FileNotFoundException(
                    $"Process start failed. yt-dlp: \"{yt}\", ffmpeg: \"{ff}\". {ex.Message}", ex);
            }

            // Pump: yt-dlp stdout -> ffmpeg stdin
            _ = Task.Run(async () =>
            {
                try
                {
                    await pYt.StandardOutput.BaseStream.CopyToAsync(pFf.StandardInput.BaseStream, 1 << 16, ct);
                    try { pFf.StandardInput.Close(); } catch { }
                }
                catch
                {
                    try { if (!pFf.HasExited) pFf.Kill(entireProcessTree: true); } catch { }
                }
            }, ct);

            // Cross-kill if either ends unexpectedly
            pYt.Exited += (_, __) => { try { if (!pFf.HasExited) pFf.Kill(entireProcessTree: true); } catch { } };
            pFf.Exited += (_, __) => { try { if (!pYt.HasExited) pYt.Kill(entireProcessTree: true); } catch { } };

            // Cancel kills both
            var reg = ct.Register(async () =>
            {
                try
                {
                    // Stop feeding ffmpeg so it can exit gracefully
                    try { pFf.StandardInput.Close(); } catch { }

                    // Stop copying task without throwing from inside the copy loop
                    // (the CopyToAsync was started with the same ct so it will unwind)
                    // Give both processes a short grace period to exit cleanly.
                    const int graceMs = 400;
                    for (int waited = 0; waited < graceMs; waited += 50)
                    {
                        if (pYt.HasExited && pFf.HasExited) break;
                        await Task.Delay(50).ConfigureAwait(false);
                    }

                    // Fallback hard-kill if they’re stubborn
                    if (!pFf.HasExited) { try { pFf.Kill(entireProcessTree: true); } catch { } }
                    if (!pYt.HasExited) { try { pYt.Kill(entireProcessTree: true); } catch { } }
                }
                catch { /* swallow */ }
            });

            return new TwoProcessOutStream(pYt, pFf, reg);

            return new TwoProcessOutStream(pYt, pFf, reg);
        }

        // --- Helpers ---------------------------------------------------------

        private static string ResolveExeOrThrow(string preferredExeName, string fallbackCmd, params string[] relativeCandidates)
        {
            // 1) App dir & provided relative locations
            foreach (string candidate in relativeCandidates.Prepend(preferredExeName))
            {
                string abs = Path.IsPathRooted(candidate) ? candidate : Path.Combine(AppContext.BaseDirectory, candidate);
                if (File.Exists(abs))
                    return abs;
            }

            // 2) PATH
            string? fromPath = FindOnPath(preferredExeName) ?? FindOnPath(fallbackCmd);
            if (fromPath != null)
                return fromPath;

            throw new FileNotFoundException($"{preferredExeName} not found. Searched: {string.Join(", ", relativeCandidates.Prepend(preferredExeName))} and PATH.");
        }

        private static string? FindOnPath(string command)
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in pathVar.Split(Path.PathSeparator).Where(d => !string.IsNullOrWhiteSpace(d)))
            {
                try
                {
                    string full = Path.Combine(dir.Trim(), command);
                    if (OperatingSystem.IsWindows() && !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        full += ".exe";
                    if (File.Exists(full))
                        return full;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Wraps the ffmpeg stdout stream, owns both yt-dlp and ffmpeg processes.
        /// Dispose / DisposeAsync or KillProcesses() immediately tear everything down.
        /// </summary>
        public sealed class TwoProcessOutStream : Stream
        {
            private readonly Process _pYt, _pFf;
            private readonly IDisposable _reg;
            private readonly Stream _inner;
            private volatile bool _killed;

            public TwoProcessOutStream(Process pYt, Process pFf, IDisposable reg)
            {
                _pYt = pYt; _pFf = pFf; _reg = reg;
                _inner = pFf.StandardOutput.BaseStream;
            }

            /// <summary>Immediately kill both processes and close the pipe.</summary>
            public void KillProcesses()
            {
                _killed = true;
                try { if (!_pFf.HasExited) _pFf.Kill(entireProcessTree: true); } catch { }
                try { if (!_pYt.HasExited) _pYt.Kill(entireProcessTree: true); } catch { }
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count)
            {
                try { return _inner.Read(buffer, offset, count); }
                catch (System.ComponentModel.Win32Exception) when (_killed) { return 0; }
                catch (IOException) when (_killed) { return 0; }
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsyncCore(buffer, offset, count, cancellationToken);

#if NET8_0_OR_GREATER
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => ReadAsyncCore(buffer, cancellationToken);
#endif

            private async Task<int> ReadAsyncCore(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                try { return await _inner.ReadAsync(buffer, offset, count, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
                catch (System.ComponentModel.Win32Exception) when (_killed) { return 0; }
                catch (IOException) when (_killed) { return 0; }
            }

#if NET8_0_OR_GREATER
            private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken ct)
            {
                try { return await _inner.ReadAsync(buffer, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
                catch (System.ComponentModel.Win32Exception) when (_killed) { return 0; }
                catch (IOException) when (_killed) { return 0; }
            }
#endif

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                try { _inner.Dispose(); } catch { }
                KillProcesses();
                try { _reg.Dispose(); } catch { }
                try { _pFf.Dispose(); } catch { }
                try { _pYt.Dispose(); } catch { }
                base.Dispose(disposing);
            }

#if NET8_0_OR_GREATER
            public override async ValueTask DisposeAsync()
            {
                try { await _inner.DisposeAsync(); } catch { }
                KillProcesses();
                try { _reg.Dispose(); } catch { }
                try { _pFf.Dispose(); } catch { }
                try { _pYt.Dispose(); } catch { }
                await base.DisposeAsync();
            }
#endif
        }

        public sealed record MediaMeta(string? Title, string? Thumbnail, TimeSpan? Duration);

        public static async Task<MediaMeta> ResolveMetadataAsync(string pageUrl, CancellationToken ct)
        {
            string yt = ResolveExeOrThrow("yt-dlp.exe", "yt-dlp", "yt-dlp.exe");
            string cookies = Path.Combine(AppContext.BaseDirectory, "cookies.txt");
            string cookieArg = File.Exists(cookies) ? $" --cookies \"{cookies}\"" : string.Empty;

            var psi = new ProcessStartInfo
            {
                FileName = yt,
                Arguments = $"--dump-json --no-playlist --no-warnings{cookieArg} \"{pageUrl}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            try
            {
                if (!p.Start())
                    throw new InvalidOperationException("Failed to start yt-dlp.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new FileNotFoundException($"Failed to start yt-dlp at \"{yt}\". {ex.Message}", ex);
            }

            #if NET8_0_OR_GREATER
                string stdout = await p.StandardOutput.ReadToEndAsync(ct);
                string stderr = await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
            #else
                string stdout = await p.StandardOutput.ReadToEndAsync();
                string stderr = await p.StandardError.ReadToEndAsync();
                p.WaitForExit();
            #endif

            if (p.ExitCode != 0)
                throw new InvalidOperationException($"yt-dlp exited with {p.ExitCode}: {stderr}");

            // Minimal parse to avoid a full DTO:
            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            string? title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            string? thumb = root.TryGetProperty("thumbnail", out var th) ? th.GetString() : null;
            TimeSpan? dur = null;
            if (root.TryGetProperty("duration", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                if (d.TryGetInt32(out int seconds) && seconds > 0)
                    dur = TimeSpan.FromSeconds(seconds);
            }

            return new MediaMeta(title, thumb, dur);
        }
    }
}
