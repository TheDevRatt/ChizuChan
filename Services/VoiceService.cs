using ChizuChan.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using static ChizuChan.Services.Interfaces.Track;

namespace ChizuChan.Services
{
    public sealed class VoiceService : IVoiceService, IAsyncDisposable
    {
        private readonly ConcurrentDictionary<ulong, PlayerActor> _players = new();
        private readonly ILogger<VoiceService> _logger;

        public VoiceService(ILogger<VoiceService> logger) => _logger = logger;

        // Events bubble from player actors
        public event Action<ulong, Track>? TrackStarted;
        public event Action<ulong, Track>? TrackEnded;
        public event Action<ulong>? QueueBecameEmpty;
        public event Action<ulong, Exception>? PlaybackError;
        public event Action<ulong>? VoiceDisconnected;

        public async Task<bool> JoinAsync(
            ulong guildId,
            ulong voiceChannelId,
            Func<CancellationToken, Task<IVoiceConnectionAdapter>> connect,
            CancellationToken ct = default)
        {
            var actor = _players.GetOrAdd(guildId, g => new PlayerActor(g, _logger,
                onStarted: t => TrackStarted?.Invoke(g, t),
                onEnded: t => TrackEnded?.Invoke(g, t),
                onEmpty: () => QueueBecameEmpty?.Invoke(g),
                onError: ex => PlaybackError?.Invoke(g, ex)));

            // Let caller own the gateway join; we only accept the adapter and attach.
            IVoiceConnectionAdapter adapter = await connect(ct);
            return await actor.AttachAsync(adapter, ct);
        }

        public async Task<bool> LeaveAsync(ulong guildId, CancellationToken ct = default)
        {
            if (_players.TryRemove(guildId, out var actor))
            {
                try { await actor.DisposeAsync(); }
                finally { VoiceDisconnected?.Invoke(guildId); }
                return true;
            }
            return false;
        }

        public Task EnqueueAsync(ulong guildId, Track track, CancellationToken ct = default)
            => Require(guildId).EnqueueAsync(track, ct);

        public Task<bool> PlayNowAsync(ulong guildId, Track track, CancellationToken ct = default)
            => Require(guildId).PlayNowAsync(track, ct);

        public Task<bool> SkipAsync(ulong guildId, CancellationToken ct = default)
            => Require(guildId).SkipAsync(ct);

        public Task<bool> StopAsync(ulong guildId, CancellationToken ct = default)
            => Require(guildId).StopAsync(ct);

        public Task<bool> PauseAsync(ulong guildId, CancellationToken ct = default)
            => Require(guildId).PauseAsync(ct);

        public Task<bool> ResumeAsync(ulong guildId, CancellationToken ct = default)
            => Require(guildId).ResumeAsync(ct);

        public Task<bool> SeekAsync(ulong guildId, TimeSpan position, CancellationToken ct = default)
            => Require(guildId).SeekAsync(position, ct);

        public Task<bool> SetVolumeAsync(ulong guildId, double volume, CancellationToken ct = default)
            => Require(guildId).SetVolumeAsync(volume, ct);

        public Task<TimeSpan?> GetPositionAsync(ulong guildId, CancellationToken ct = default)
            => Require(guildId).GetPositionAsync(ct);

        public async Task<QueueSnapshot> GetSnapshotAsync(ulong guildId, CancellationToken ct = default)
        {
            if (_players.TryGetValue(guildId, out var actor))
            {
                return await actor.GetSnapshotAsync(ct);
            }

            // No player exists yet → return a disconnected snapshot
            return new QueueSnapshot(
                current: null,
                upcoming: Array.Empty<Track>(),
                isPaused: false,
                volume: 1.0,
                isConnected: false,
                canSkip: false);
        }

        private PlayerActor Require(ulong guildId)
        {
            if (!_players.TryGetValue(guildId, out var actor))
                throw new InvalidOperationException("Guild is not connected. Call JoinAsync first.");
            return actor;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var kv in _players)
                try { await kv.Value.DisposeAsync(); } catch { }
            _players.Clear();
        }

        // ====================================================================
        // =                         Player Actor                              =
        // ====================================================================

        private sealed class PlayerActor : IAsyncDisposable
        {
            private readonly ulong _guildId;
            private readonly ILogger _log;
            private readonly Action<Track>? _onStarted;
            private readonly Action<Track>? _onEnded;
            private readonly Action? _onEmpty;
            private readonly Action<Exception>? _onError;
            private Guid _opId = Guid.Empty;           // new operation id per track
            private readonly Stopwatch _diag = new();  // reused for relative timings

            // Single-threaded mailbox
            private readonly Channel<IMessage> _mb = Channel.CreateUnbounded<IMessage>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

            // State owned by the actor thread
            private IVoiceConnectionAdapter? _conn;
            private readonly ConcurrentQueue<Track> _queue = new();
            private Track? _current;
            private Track? _nextOverride;

            private double _volume = 1.0; // 0..1
            private volatile bool _paused;
            private readonly SemaphoreSlim _pauseGate = new(1, 1); // held when playing; wait when paused

            // Playback loop state (shared with playback task)
            private CancellationTokenSource? _trackCts;
            private Stream? _sink;
            private Stream? _pcm;

            private readonly Stopwatch _posWatch = new();
            private TimeSpan _posOffset;

            private Task? _actorLoop;
            private Task? _playLoop;
            private readonly CancellationTokenSource _lifects = new();

            public PlayerActor(ulong guildId,
                               ILogger logger,
                               Action<Track>? onStarted,
                               Action<Track>? onEnded,
                               Action? onEmpty,
                               Action<Exception>? onError)
            {
                _guildId = guildId;
                _log = logger;
                _onStarted = onStarted;
                _onEnded = onEnded;
                _onEmpty = onEmpty;
                _onError = onError;

                _actorLoop = Task.Run(RunActorAsync);
            }

            // ---------------- Public API (post to mailbox) ----------------

            public Task<bool> AttachAsync(IVoiceConnectionAdapter conn, CancellationToken ct)
                => Ask(new Msg.Attach(conn), ct);

            public Task EnqueueAsync(Track t, CancellationToken ct)
                => Tell(new Msg.Enqueue(t), ct);

            public Task<bool> PlayNowAsync(Track t, CancellationToken ct)
                => Ask(new Msg.PlayNow(t), ct);

            public Task<bool> SkipAsync(CancellationToken ct)
                => Ask(new Msg.Skip(), ct);

            public Task<bool> StopAsync(CancellationToken ct)
                => Ask(new Msg.Stop(), ct);

            public Task<bool> PauseAsync(CancellationToken ct)
                => Ask(new Msg.Pause(), ct);

            public Task<bool> ResumeAsync(CancellationToken ct)
                => Ask(new Msg.Resume(), ct);

            public Task<bool> SeekAsync(TimeSpan pos, CancellationToken ct)
                => Ask(new Msg.Seek(pos), ct);

            public Task<bool> SetVolumeAsync(double v, CancellationToken ct)
                => Ask(new Msg.SetVolume(v), ct);

            public Task<TimeSpan?> GetPositionAsync(CancellationToken ct)
                => Ask(new Msg.GetPos(), ct);

            public Task<QueueSnapshot> GetSnapshotAsync(CancellationToken ct)
                => Ask(new Msg.Snapshot(), ct);

            // ---------------- Actor loop & helpers ----------------

            private async Task RunActorAsync()
            {
                try
                {
                    while (await _mb.Reader.WaitToReadAsync(_lifects.Token).ConfigureAwait(false))
                    {
                        while (_mb.Reader.TryRead(out var msg))
                        {
                            try { await HandleAsync(msg).ConfigureAwait(false); }
                            catch (Exception ex)
                            {
                                _log.LogError(ex, "Actor message failed (guild={GuildId})", _guildId);
                                _onError?.Invoke(ex);
                                msg.Fail(ex);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { /* shutting down */ }
            }

            private async Task HandleAsync(IMessage message)
            {
                switch (message)
                {
                    case Msg.Attach m:
                        _conn = m.Connection;
                        message.Ok(true);
                        break;

                    case Msg.Enqueue m:
                        _queue.Enqueue(m.Track);
                        EnsurePlayLoop();
                        message.Ok();
                        break;

                    case Msg.PlayNow m:
                        _nextOverride = m.Track;
                        // Skip current if playing so the override starts immediately
                        _trackCts?.Cancel();
                        EnsurePlayLoop();
                        message.Ok(true);
                        break;

                    case Msg.Skip:
                        if (_current is null) { message.Ok(false); break; }
                        var skipId = Guid.NewGuid();
                        _log.LogInformation("[{Guild}] SKIP requested -> op={Op}", _guildId, skipId);
                        try { _trackCts?.Cancel(); }
                        catch (Exception ex) { _log.LogWarning(ex, "[{Guild}] SKIP cancel threw -> op={Op}", _guildId, skipId); }
                        message.Ok(true);
                        break;

                    case Msg.Stop:
                        while (_queue.TryDequeue(out _)) { }
                        _nextOverride = null;
                        _trackCts?.Cancel();
                        message.Ok(true);
                        break;

                    case Msg.Pause:
                        if (_paused) { message.Ok(true); break; }
                        _paused = true;
                        // take the gate so playback will wait
                        await _pauseGate.WaitAsync().ConfigureAwait(false);
                        if (_posWatch.IsRunning)
                        {
                            _posOffset += _posWatch.Elapsed;
                            _posWatch.Reset();
                        }
                        message.Ok(true);
                        break;

                    case Msg.Resume:
                        if (!_paused) { message.Ok(true); break; }
                        _paused = false;
                        if (!_posWatch.IsRunning) _posWatch.Start();
                        TryReleaseGate();
                        message.Ok(true);
                        break;

                    case Msg.SetVolume m:
                        if (m.Volume < 0) m.Volume = 0;
                        if (m.Volume > 1) m.Volume = 1;
                        _volume = m.Volume;
                        message.Ok(true);
                        break;

                    case Msg.Seek:
                        // Not implemented (needs re-open w/ -ss)
                        message.Ok(false);
                        break;

                    case Msg.GetPos:
                        message.Ok(GetPositionCore());
                        break;

                    case Msg.Snapshot:
                        {
                            var up = _queue.ToArray();

                            // Can skip only when a track is currently playing AND there is something to advance to:
                            // either a PlayNow override or at least one queued item.
                            bool hasCurrent = _current is not null;
                            bool canSkip = hasCurrent && (_nextOverride is not null || up.Length > 0);

                            message.Ok(new QueueSnapshot(
                                current: _current,
                                upcoming: up,
                                isPaused: _paused,
                                volume: _volume,
                                isConnected: _conn?.IsConnected == true,
                                canSkip: canSkip));
                        }
                        break;

                    default:
                        message.Fail(new NotSupportedException(message.GetType().Name));
                        break;
                }
            }

            private void EnsurePlayLoop()
            {
                if (_playLoop is { IsCompleted: false }) return;
                _playLoop = Task.Run(RunPlaybackLoopAsync);
            }

            private async Task RunPlaybackLoopAsync()
            {
                var sessionCt = _lifects.Token;

                try
                {
                    while (!sessionCt.IsCancellationRequested)
                    {
                        if (_conn is null || !_conn.IsConnected)
                        {
                            await Task.Delay(200, sessionCt).ConfigureAwait(false);
                            continue;
                        }

                        // pick next track
                        Track? next = _nextOverride;
                        _nextOverride = null;
                        if (next is null && !_queue.TryDequeue(out next))
                        {
                            // nothing to do; tell UI once
                            _onEmpty?.Invoke();
                            return;
                        }

                        _current = next;
                        _opId = Guid.NewGuid();       // correlate everything for this track
                        _diag.Restart();
                        _onStarted?.Invoke(next);

                        _log.LogInformation("[{Guild}] ▶ START track='{Title}' op={Op}", _guildId, next.Title ?? "(null)", _opId);

                        _posOffset = TimeSpan.Zero;
                        _posWatch.Restart();

                        using var trackCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
                        _trackCts = trackCts;

                        Stream? sink = null;
                        Stream? pcm = null;
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);

                        try
                        {
                            var t0 = _diag.Elapsed;
                            sink = await _conn!.OpenPcmSinkAsync(trackCts.Token).ConfigureAwait(false);
                            var t1 = _diag.Elapsed;
                            _log.LogInformation("[{Guild}] sink opened in {Ms} ms op={Op}", _guildId, (t1 - t0).TotalMilliseconds, _opId);

                            pcm = await OpenPcmAsync(next, trackCts.Token).ConfigureAwait(false);
                            var t2 = _diag.Elapsed;
                            _log.LogInformation("[{Guild}] pcm opened in {Ms} ms (cumulative={Cum} ms) op={Op}",
                                _guildId, (t2 - t1).TotalMilliseconds, t2.TotalMilliseconds, _opId);

                            _sink = sink; _pcm = pcm;

                            bool loggedFirstWrite = false;

                            while (true)
                            {
                                if (_paused)
                                {
                                    _log.LogDebug("[{Guild}] paused -> waiting gate op={Op}", _guildId, _opId);
                                    await _pauseGate.WaitAsync(trackCts.Token).ConfigureAwait(false);
                                    TryReleaseGate();
                                    _log.LogDebug("[{Guild}] resumed -> gate released op={Op}", _guildId, _opId);
                                }

                                int read;
                                try
                                {
                                    read = await pcm.ReadAsync(buffer, 0, buffer.Length, trackCts.Token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (trackCts.IsCancellationRequested)
                                {
                                    _log.LogInformation("[{Guild}] read canceled (skip/stop) at {Ms} ms op={Op}",
                                        _guildId, _diag.Elapsed.TotalMilliseconds, _opId);
                                    break;
                                }
                                catch (ObjectDisposedException)   // <<< add this
                                {
                                    // stream got closed during cancel/teardown; treat as EOF
                                    break;
                                }
                                catch (System.ComponentModel.Win32Exception ex)
                                {
                                    _log.LogInformation(ex, "[{Guild}] read Win32Exception -> treat as EOF at {Ms} ms op={Op}",
                                        _guildId, _diag.Elapsed.TotalMilliseconds, _opId);
                                    break;
                                }
                                catch (IOException ex)
                                {
                                    _log.LogInformation(ex, "[{Guild}] read IOException -> treat as EOF at {Ms} ms op={Op}",
                                        _guildId, _diag.Elapsed.TotalMilliseconds, _opId);
                                    break;
                                }

                                if (read <= 0)
                                {
                                    _log.LogInformation("[{Guild}] read=0 (EOF) at {Ms} ms op={Op}",
                                        _guildId, _diag.Elapsed.TotalMilliseconds, _opId);
                                    break;
                                }

                                if (_volume < 0.999)
                                    ApplyVolumeInPlace(buffer, read, _volume);

                                try
                                {
                                    await sink.WriteAsync(buffer.AsMemory(0, read), trackCts.Token).ConfigureAwait(false);
                                    if (!loggedFirstWrite)
                                    {
                                        loggedFirstWrite = true;
                                        _log.LogInformation("[{Guild}] first write at {Ms} ms op={Op}", _guildId, _diag.Elapsed.TotalMilliseconds, _opId);
                                    }
                                }
                                catch (OperationCanceledException) when (trackCts.IsCancellationRequested)
                                {
                                    _log.LogInformation("[{Guild}] write canceled (skip/stop) at {Ms} ms op={Op}",
                                        _guildId, _diag.Elapsed.TotalMilliseconds, _opId);
                                    break;
                                }
                                catch (ObjectDisposedException)   // <<< add this
                                {
                                    // stream got closed during cancel/teardown; treat as EOF
                                    break;
                                }
                                catch (System.ComponentModel.Win32Exception ex)
                                {
                                    _log.LogInformation(ex, "[{Guild}] write Win32Exception -> break at {Ms} ms op={Op}",
                                        _guildId, _diag.Elapsed.TotalMilliseconds, _opId);
                                    break;
                                }
                                catch (IOException ex)
                                {
                                    _log.LogInformation(ex, "[{Guild}] write IOException -> break at {Ms} ms op={Op}",
                                        _guildId, _diag.Elapsed.TotalMilliseconds, _opId);
                                    break;
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _log.LogError(ex, "[{Guild}] playback error at {Ms} ms op={Op}", _guildId, _diag.Elapsed.TotalMilliseconds, _opId);
                            _onError?.Invoke(ex);
                        }
                        finally
                        {
                            var finishedAt = _diag.Elapsed.TotalMilliseconds;

                            ArrayPool<byte>.Shared.Return(buffer);

                            _trackCts = null;
                            _posWatch.Reset();
                            _posOffset = TimeSpan.Zero;

                            try { sink?.Dispose(); } catch { }
                            try { _ = _conn?.StopSpeakingAsync(); } catch { }

                            try
                            {
                                if (pcm is IAsyncDisposable iad) await iad.DisposeAsync().ConfigureAwait(false);
                                else pcm?.Dispose();
                            }
                            catch (Exception ex)
                            {
                                _log.LogDebug(ex, "[{Guild}] pcm dispose raised op={Op}", _guildId, _opId);
                            }

                            _sink = null; _pcm = null;

                            var ended = _current;
                            _current = null;
                            if (ended is not null)
                                _log.LogInformation("[{Guild}] ⏹ END track='{Title}' total={Ms} ms op={Op}",
                                    _guildId, ended.Title ?? "(null)", finishedAt, _opId);

                            if (ended is not null) _onEnded?.Invoke(ended);
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }

            private TimeSpan? GetPositionCore()
            {
                if (_current is null) return null;
                var running = _posWatch.IsRunning ? _posWatch.Elapsed : TimeSpan.Zero;
                return _posOffset + running;
            }

            private void TryReleaseGate()
            {
                try { _pauseGate.Release(); } catch (SemaphoreFullException) { }
            }

            public async ValueTask DisposeAsync()
            {
                _lifects.Cancel();

                try
                {
                    // cancel current track
                    _trackCts?.Cancel();
                }
                catch { }

                try
                {
                    if (_playLoop is not null) await _playLoop.ConfigureAwait(false);
                }
                catch { }

                try
                {
                    if (_actorLoop is not null) await _actorLoop.ConfigureAwait(false);
                }
                catch { }

                // disconnect voice
                try { await (_conn?.DisconnectAsync(CancellationToken.None) ?? Task.CompletedTask).ConfigureAwait(false); }
                catch { }

                try { await (_conn?.DisposeAsync() ?? ValueTask.CompletedTask); } catch { }
                _conn = null;

                _pauseGate.Dispose();
                _lifects.Dispose();
            }

            // ---------------- PCM open helpers & volume ----------------

            private static Task<Stream> OpenPcmAsync(Track t, CancellationToken ct)
            {
                return t.SourceType switch
                {
                    TrackSourceType.FilePath => Task.FromResult<Stream>(SpawnFfmpegPcm($"-i \"{t.FilePath}\"", ct)),
                    TrackSourceType.Url => Task.FromResult<Stream>(SpawnFfmpegPcm($"-i \"{t.Url}\"", ct)),
                    TrackSourceType.StreamFactory => t.StreamFactory?.Invoke(ct)
                        ?? throw new ArgumentException("StreamFactory not provided."),
                    _ => throw new NotSupportedException("Unknown source type."),
                };
            }

            private static void ApplyVolumeInPlace(byte[] buffer, int count, double volume)
            {
                for (int i = 0; i < count; i += 2)
                {
                    short s = (short)(buffer[i] | (buffer[i + 1] << 8));
                    int scaled = (int)(s * volume);
                    if (scaled > short.MaxValue) scaled = short.MaxValue;
                    if (scaled < short.MinValue) scaled = short.MinValue;
                    buffer[i] = (byte)(scaled & 0xFF);
                    buffer[i + 1] = (byte)((scaled >> 8) & 0xFF);
                }
            }

            private static Stream SpawnFfmpegPcm(string input, CancellationToken ct)
            {
                string args = $"-hide_banner -loglevel quiet {input} -f s16le -ar 48000 -ac 2 pipe:1";
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                };

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.Start();

                // create wrapper first so we can mark it on cancel
                var wrapper = new KillAwareProcOut(proc, reg: null!); // allow null, see note below

                var reg = ct.Register(async () =>
                {
                    try
                    {
                        wrapper.MarkCanceled();                 // <<< tell the reader to treat post-cancel I/O as EOF

                        // Soft close
                        try { proc.CancelOutputRead(); } catch { }
                        try { proc.StandardOutput.BaseStream.Close(); } catch { }

                        // Grace period, then fallback kill if needed
                        const int graceMs = 300;
                        for (int waited = 0; waited < graceMs; waited += 50)
                        {
                            if (proc.HasExited) break;
                            await Task.Delay(50).ConfigureAwait(false);
                        }
                        if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } }
                    }
                    catch { }
                });

                // if your KillAwareProcOut currently *requires* a non-null reg in ctor,
                // add a setter like wrapper.SetRegistration(reg); and store it inside.
                return wrapper;
            }

            // ffmpeg stdout wrapper that returns EOF instead of spamming exceptions when killed
            private sealed class KillAwareProcOut : Stream
            {
                private readonly Process _proc;
                private readonly IDisposable _reg;
                private readonly Stream _inner;
                private volatile bool _killed;
                private volatile bool _canceled;

                public KillAwareProcOut(Process proc, IDisposable reg)
                {
                    _proc = proc; _reg = reg; _inner = proc.StandardOutput.BaseStream;
                }

                public void MarkCanceled() => _canceled = true;

                public void Kill() { _killed = true; try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { } }

                public override bool CanRead => _inner.CanRead;
                public override bool CanSeek => false;
                public override bool CanWrite => false;
                public override long Length => throw new NotSupportedException();
                public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
                public override void Flush() => _inner.Flush();

                public override int Read(byte[] buffer, int offset, int count)
                {
                    try { return _inner.Read(buffer, offset, count); }
                    catch (System.ComponentModel.Win32Exception) when (_killed || _canceled) { return 0; }  // broaden
                    catch (IOException) when (_killed || _canceled) { return 0; }                            // broaden
                }

                public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                    => ReadAsyncCore(buffer, offset, count, cancellationToken);

#if NET8_0_OR_GREATER
                public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                    => ReadAsyncCore(buffer, cancellationToken);
#endif
                private async Task<int> ReadAsyncCore(byte[] buffer, int offset, int count, CancellationToken ct)
                {
                    try { return await _inner.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
                    catch (System.ComponentModel.Win32Exception) when (_killed || _canceled) { return 0; }  // broaden
                    catch (IOException) when (_killed || _canceled) { return 0; }                            // broaden
                }

#if NET8_0_OR_GREATER
                private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken ct)
                {
                    try { return await _inner.ReadAsync(buffer, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
                    catch (System.ComponentModel.Win32Exception) when (_killed || _canceled) { return 0; }  // broaden
                    catch (IOException) when (_killed || _canceled) { return 0; }                            // broaden
                }
#endif
                public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value) => throw new NotSupportedException();
                public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

                protected override void Dispose(bool disposing)
                {
                    try { _inner.Dispose(); } catch { }
                    Kill();
                    try { _reg.Dispose(); } catch { }
                    try { _proc.Dispose(); } catch { }
                    base.Dispose(disposing);
                }

#if NET8_0_OR_GREATER
                public override async ValueTask DisposeAsync()
                {
                    try { await _inner.DisposeAsync().ConfigureAwait(false); } catch { }
                    Kill();
                    try { _reg.Dispose(); } catch { }
                    try { _proc.Dispose(); } catch { }
                    await base.DisposeAsync();
                }
#endif
            }

            // ---------------- Mailbox plumbing ----------------

            private interface IMessage
            {
                void Ok();
                void Ok<T>(T value);
                void Fail(Exception ex);
            }

            private abstract class AskBase<T> : IMessage
            {
                public readonly TaskCompletionSource<T> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                public void Ok() => Tcs.TrySetResult(default!);
                public void Ok<TOut>(TOut value)
                {
                    if (value is T cast) Tcs.TrySetResult(cast);
                    else Tcs.TrySetException(new InvalidCastException());
                }
                public void Fail(Exception ex) => Tcs.TrySetException(ex);
            }

            private abstract class TellBase : IMessage
            {
                public readonly TaskCompletionSource<bool> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                public void Ok() => Tcs.TrySetResult(true);
                public void Ok<T>(T _) => Tcs.TrySetResult(true);
                public void Fail(Exception ex) => Tcs.TrySetException(ex);
            }

            private static Task<bool> Tell(IMessage msg, CancellationToken ct) =>
                Post(msg, ct).ContinueWith(t => true, TaskScheduler.Default);

            private static Task<T> Ask<T>(IMessage msg, CancellationToken ct)
            {
                if (msg is AskBase<T> ask) return Post(msg, ct).ContinueWith(_ => ask.Tcs.Task).Unwrap();
                throw new InvalidOperationException("Bad ask type");
            }

            private static Task Post(IMessage msg, CancellationToken ct)
            {
                // Will be replaced at instance level; this indirection lets us keep helpers static
                throw new NotSupportedException();
            }

            // Instance-bound Post overloads
            private Task<bool> Tell(TellBase msg, CancellationToken ct)
                => WriteToMailbox(msg, ct).ContinueWith(t => msg.Tcs.Task).Unwrap();

            private Task<T> Ask<T>(AskBase<T> msg, CancellationToken ct)
                => WriteToMailbox(msg, ct).ContinueWith(t => msg.Tcs.Task).Unwrap();

            private async Task WriteToMailbox(IMessage msg, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                await _mb.Writer.WriteAsync(msg, ct).ConfigureAwait(false);
            }

            private static class Msg
            {
                internal sealed class Attach : AskBase<bool> { public readonly IVoiceConnectionAdapter Connection; public Attach(IVoiceConnectionAdapter c) => Connection = c; }
                internal sealed class Enqueue : TellBase { public readonly Track Track; public Enqueue(Track t) => Track = t; }
                internal sealed class PlayNow : AskBase<bool> { public readonly Track Track; public PlayNow(Track t) => Track = t; }
                internal sealed class Skip : AskBase<bool> { }
                internal sealed class Stop : AskBase<bool> { }
                internal sealed class Pause : AskBase<bool> { }
                internal sealed class Resume : AskBase<bool> { }
                internal sealed class Seek : AskBase<bool> { public readonly TimeSpan Pos; public Seek(TimeSpan p) => Pos = p; }
                internal sealed class SetVolume : AskBase<bool> { public double Volume; public SetVolume(double v) => Volume = v; }
                internal sealed class GetPos : AskBase<TimeSpan?> { }
                internal sealed class Snapshot : AskBase<QueueSnapshot> { }
            }
        }
    }
}
