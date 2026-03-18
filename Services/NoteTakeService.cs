using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Rest;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ChizuChan.Services
{
    /// <summary>
    /// Listens for incoming voice packets, transcribes them with Whisper, and writes
    /// a rolling timestamped log — no AI response, no TTS.  Designed for D&D sessions.
    ///
    /// Pipeline per utterance:
    ///   Opus packets  →  OpusDecoder (48 kHz stereo s16le)
    ///   →  ffmpeg (resample → 16 kHz mono WAV)
    ///   →  Whisper.net STT
    ///   →  append to session log file + post to text channel
    /// </summary>
    public class NoteTakeService : INoteTakeService
    {
        private readonly ConcurrentDictionary<ulong, GuildRecorder> _recorders = new();

        private readonly IWhisperSttService _whisper;
        private readonly RestClient _restClient;
        private readonly GatewayClient _gatewayClient;
        private readonly ILogger<NoteTakeService> _logger;

        public NoteTakeService(
            IWhisperSttService whisper,
            RestClient restClient,
            GatewayClient gatewayClient,
            ILogger<NoteTakeService> logger)
        {
            _whisper = whisper;
            _restClient = restClient;
            _gatewayClient = gatewayClient;
            _logger = logger;
        }

        public void StartRecording(ulong guildId, ulong textChannelId, VoiceClient voiceClient)
        {
            var recorder = new GuildRecorder(
                guildId, textChannelId, voiceClient,
                _whisper, _restClient, _gatewayClient, _logger);

            if (_recorders.TryAdd(guildId, recorder))
            {
                recorder.Start();
                _logger.LogInformation("[NoteTake] Started recording in guild={GuildId}, log={Path}",
                    guildId, recorder.LogPath);
            }
        }

        public bool IsRecording(ulong guildId) => _recorders.ContainsKey(guildId);

        public string? GetLogPath(ulong guildId) =>
            _recorders.TryGetValue(guildId, out var r) ? r.LogPath : null;

        public void StopRecording(ulong guildId)
        {
            if (_recorders.TryRemove(guildId, out var recorder))
            {
                recorder.Dispose();
                _logger.LogInformation("[NoteTake] Stopped recording in guild={GuildId}", guildId);
            }
        }

        // =====================================================================
        // Per-guild recorder
        // =====================================================================

        private sealed class GuildRecorder : IDisposable
        {
            private readonly ulong _guildId;
            private readonly ulong _textChannelId;
            private readonly VoiceClient _vc;
            private readonly IWhisperSttService _whisper;
            private readonly RestClient _restClient;
            private readonly GatewayClient _gatewayClient;
            private readonly ILogger _logger;

            private readonly ConcurrentDictionary<uint, ulong> _ssrcToUser = new();
            private readonly ConcurrentDictionary<uint, UserBuffer> _buffers = new();
            private readonly ConcurrentDictionary<ulong, string> _usernameCache = new();

            private readonly StreamWriter _logWriter;
            private readonly object _logLock = new();

            private bool _disposed;

            public string LogPath { get; }

            public GuildRecorder(
                ulong guildId, ulong textChannelId, VoiceClient vc,
                IWhisperSttService whisper, RestClient restClient,
                GatewayClient gatewayClient, ILogger logger)
            {
                _guildId = guildId;
                _textChannelId = textChannelId;
                _vc = vc;
                _whisper = whisper;
                _restClient = restClient;
                _gatewayClient = gatewayClient;
                _logger = logger;

                // Create notes directory next to the executable
                string notesDir = Path.Combine(AppContext.BaseDirectory, "notes");
                Directory.CreateDirectory(notesDir);

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                LogPath = Path.Combine(notesDir, $"session_{guildId}_{timestamp}.txt");

                _logWriter = new StreamWriter(LogPath, append: false, System.Text.Encoding.UTF8)
                {
                    AutoFlush = true
                };

                _logWriter.WriteLine($"# D&D Session Log — started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _logWriter.WriteLine($"# Guild: {guildId}");
                _logWriter.WriteLine();
            }

            public void Start()
            {
                _vc.Speaking += OnSpeaking;
                _vc.VoiceReceive += OnVoiceReceive;
            }

            private ValueTask OnSpeaking(SpeakingEventArgs args)
            {
                if (args.UserId != 0 && args.Ssrc != 0)
                    _ssrcToUser[args.Ssrc] = args.UserId;
                return ValueTask.CompletedTask;
            }

            private ValueTask OnVoiceReceive(VoiceReceiveEventArgs args)
            {
                if (_disposed) return ValueTask.CompletedTask;

                var packet = args.Frame.ToArray();
                if (packet.Length == 0) return ValueTask.CompletedTask;

                var ssrc = args.Ssrc;
                var buffer = _buffers.GetOrAdd(ssrc, s => new UserBuffer(s,
                    async (s2, packets) =>
                    {
                        if (!_disposed)
                            await ProcessUtteranceAsync(s2, packets);
                    },
                    _logger));

                buffer.AddPacket(packet);
                return ValueTask.CompletedTask;
            }

            private async Task ProcessUtteranceAsync(uint ssrc, byte[][] packets)
            {
                _ssrcToUser.TryGetValue(ssrc, out ulong userId);
                string? username = await GetUsernameAsync(userId);

                // 1. Decode Opus → raw PCM
                byte[] rawPcm = DecodeOpusToPcm(packets);
                if (rawPcm.Length < 19200) return; // skip < ~200 ms

                // 2. Resample to 16 kHz mono WAV
                byte[] wavBytes = await ResampleToWhisperWavAsync(rawPcm);
                if (wavBytes.Length == 0) return;

                // 3. Transcribe
                var transcript = await _whisper.TranscribeAsync(wavBytes);
                if (string.IsNullOrWhiteSpace(transcript)) return;

                string speaker = username ?? userId.ToString();
                string timeStamp = DateTime.Now.ToString("HH:mm:ss");

                _logger.LogInformation("[NoteTake] [{Time}] {Speaker}: {Transcript}",
                    timeStamp, speaker, transcript);

                // 4. Append to log file
                string logLine = $"[{timeStamp}] {speaker}: {transcript}";
                lock (_logLock)
                {
                    try { _logWriter.WriteLine(logLine); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[NoteTake] Failed to write to log file");
                    }
                }

                // 5. Post to text channel
                try
                {
                    string who = userId > 0 ? $"<@{userId}>" : speaker;
                    await _restClient.SendMessageAsync(_textChannelId, new MessageProperties
                    {
                        Content = $"📝 `{timeStamp}` **{who}**: {transcript}"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[NoteTake] Failed to post transcript to channel");
                }
            }

            private async Task<string?> GetUsernameAsync(ulong userId)
            {
                if (userId == 0) return null;
                if (_usernameCache.TryGetValue(userId, out var cached)) return cached;
                try
                {
                    var user = await _restClient.GetUserAsync(userId);
                    _usernameCache[userId] = user.Username;
                    return user.Username;
                }
                catch
                {
                    return null;
                }
            }

            private static byte[] DecodeOpusToPcm(byte[][] packets)
            {
                const int FrameSamplesPerChannel = 960;
                const int Channels = 2;

                using var decoder = new OpusDecoder(VoiceChannels.Stereo);
                using var ms = new MemoryStream(packets.Length * FrameSamplesPerChannel * Channels * sizeof(short));

                var pcmShorts = new short[FrameSamplesPerChannel * Channels];
                var pcmBytes = new byte[pcmShorts.Length * sizeof(short)];

                foreach (var packet in packets)
                {
                    try
                    {
                        int decoded = decoder.Decode(packet, pcmShorts.AsSpan(), FrameSamplesPerChannel);
                        int byteCount = decoded * Channels * sizeof(short);
                        Buffer.BlockCopy(pcmShorts, 0, pcmBytes, 0, byteCount);
                        ms.Write(pcmBytes, 0, byteCount);
                    }
                    catch { /* skip bad packet */ }
                }

                return ms.ToArray();
            }

            private static async Task<byte[]> ResampleToWhisperWavAsync(byte[] rawPcm48kHzStereo)
            {
                var psi = new ProcessStartInfo("ffmpeg")
                {
                    Arguments = "-f s16le -ar 48000 -ac 2 -i pipe:0 " +
                                "-f wav -ar 16000 -ac 1 pipe:1 -loglevel quiet",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var writeTask = Task.Run(async () =>
                {
                    await proc.StandardInput.BaseStream.WriteAsync(rawPcm48kHzStereo);
                    proc.StandardInput.BaseStream.Close();
                });

                using var outputMs = new MemoryStream();
                await proc.StandardOutput.BaseStream.CopyToAsync(outputMs);
                await writeTask;
                await proc.WaitForExitAsync();

                return outputMs.ToArray();
            }

            public void Dispose()
            {
                _disposed = true;
                try { _vc.Speaking -= OnSpeaking; } catch { }
                try { _vc.VoiceReceive -= OnVoiceReceive; } catch { }
                foreach (var buf in _buffers.Values)
                    buf.Dispose();
                _buffers.Clear();

                lock (_logLock)
                {
                    try { _logWriter.Dispose(); } catch { }
                }
            }
        }

        // =====================================================================
        // Per-SSRC audio accumulator with silence-based VAD
        // =====================================================================

        private sealed class UserBuffer : IDisposable
        {
            private readonly uint _ssrc;
            private readonly Func<uint, byte[][], Task> _onUtterance;
            private readonly ILogger _logger;

            private readonly List<byte[]> _packets = new();
            private readonly object _lock = new();
            private CancellationTokenSource? _silenceCts;

            private const int SilenceMs = 800;
            private const int MaxPackets = 600;

            public UserBuffer(uint ssrc, Func<uint, byte[][], Task> onUtterance, ILogger logger)
            {
                _ssrc = ssrc;
                _onUtterance = onUtterance;
                _logger = logger;
            }

            public void AddPacket(byte[] packet)
            {
                lock (_lock)
                {
                    if (_packets.Count >= MaxPackets)
                        _packets.RemoveAt(0);

                    _packets.Add(packet);

                    _silenceCts?.Cancel();
                    _silenceCts?.Dispose();
                    var cts = _silenceCts = new CancellationTokenSource();

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(SilenceMs, cts.Token);
                            FireUtterance();
                        }
                        catch (OperationCanceledException) { }
                    }, CancellationToken.None);
                }
            }

            private void FireUtterance()
            {
                byte[][] captured;
                lock (_lock)
                {
                    if (_packets.Count == 0) return;
                    captured = _packets.ToArray();
                    _packets.Clear();
                }
                _ = _onUtterance(_ssrc, captured);
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    _silenceCts?.Cancel();
                    _silenceCts?.Dispose();
                    _silenceCts = null;
                    _packets.Clear();
                }
            }
        }
    }
}
