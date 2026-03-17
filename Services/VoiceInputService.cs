using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Rest;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Speech.Synthesis;
using static ChizuChan.Services.Interfaces.Track;

namespace ChizuChan.Services
{
    /// <summary>
    /// Listens for incoming voice packets, transcribes them with Whisper, sends the
    /// transcript to Ollama, and plays the TTS response back through the voice channel.
    ///
    /// Pipeline per utterance (serialised per guild — no self-interruption):
    ///   Opus packets  →  NetCord OpusDecoder (48 kHz stereo s16le)
    ///   →  ffmpeg (resample → 16 kHz mono WAV)
    ///   →  Whisper.net STT
    ///   →  IOllamaService  (message prefixed with speaker name)
    ///   →  edge-tts (JennyNeural) if installed, else System.Speech female
    ///   →  ffmpeg (audio → 48 kHz stereo s16le PCM)
    ///   →  IVoiceService.PlayNowAsync
    ///
    /// TTS quality:
    ///   Install edge-tts for natural neural voice: pip install edge-tts
    ///   Falls back to Windows SAPI female voice automatically.
    ///
    /// Voice can be changed via appsettings.json  "VoiceAi": { "EdgeTtsVoice": "en-US-AriaNeural" }
    /// Full list: https://docs.microsoft.com/en-us/azure/cognitive-services/speech-service/language-support
    /// </summary>
    public class VoiceInputService : IVoiceInputService
    {
        // Change this to any en-US-*Neural voice from the Azure voice list.
        // Good female options: JennyNeural, AriaNeural, JaneNeural, NancyNeural
        private const string EdgeTtsVoice = "en-US-JennyNeural";

        private readonly ConcurrentDictionary<ulong, GuildListener> _listeners = new();

        private readonly IOllamaService _ollama;
        private readonly IVoiceService _voiceService;
        private readonly RestClient _restClient;
        private readonly GatewayClient _gatewayClient;
        private readonly IWhisperSttService _whisper;
        private readonly ILogger<VoiceInputService> _logger;

        public VoiceInputService(
            IOllamaService ollama,
            IVoiceService voiceService,
            RestClient restClient,
            GatewayClient gatewayClient,
            IWhisperSttService whisper,
            ILogger<VoiceInputService> logger)
        {
            _ollama = ollama;
            _voiceService = voiceService;
            _restClient = restClient;
            _gatewayClient = gatewayClient;
            _whisper = whisper;
            _logger = logger;
        }

        public void StartListening(ulong guildId, ulong textChannelId, VoiceClient voiceClient)
        {
            var listener = new GuildListener(
                guildId, textChannelId, voiceClient,
                _ollama, _voiceService, _restClient, _gatewayClient, _whisper, _logger);

            if (_listeners.TryAdd(guildId, listener))
            {
                listener.Start();
                _logger.LogInformation("[VoiceInput] Started listening in guild={GuildId}", guildId);
            }
        }

        public bool IsListening(ulong guildId) => _listeners.ContainsKey(guildId);

        public void StopListening(ulong guildId)
        {
            if (_listeners.TryRemove(guildId, out var listener))
            {
                listener.Dispose();
                _logger.LogInformation("[VoiceInput] Stopped listening in guild={GuildId}", guildId);
            }
        }

        // =====================================================================
        // Per-guild listener
        // =====================================================================

        private sealed class GuildListener : IDisposable
        {
            private readonly ulong _guildId;
            private readonly ulong _textChannelId;
            private readonly VoiceClient _vc;
            private readonly IOllamaService _ollama;
            private readonly IVoiceService _voiceService;
            private readonly RestClient _restClient;
            private readonly GatewayClient _gatewayClient;
            private readonly IWhisperSttService _whisper;
            private readonly ILogger _logger;

            // SSRC → UserId, populated from Speaking events
            private readonly ConcurrentDictionary<uint, ulong> _ssrcToUser = new();

            // Per-SSRC audio accumulators
            private readonly ConcurrentDictionary<uint, UserBuffer> _buffers = new();

            // Username cache so we don't REST-call repeatedly for the same user
            private readonly ConcurrentDictionary<ulong, string> _usernameCache = new();

            // Serialise pipeline: only one utterance processed + played at a time per guild
            private readonly SemaphoreSlim _responseQueue = new(1, 1);

            // Suppress incoming audio while the bot is speaking TTS
            private long _ignoreUntilMs = 0;

            private bool _disposed;

            public GuildListener(
                ulong guildId, ulong textChannelId, VoiceClient vc,
                IOllamaService ollama, IVoiceService voiceService,
                RestClient restClient, GatewayClient gatewayClient,
                IWhisperSttService whisper, ILogger logger)
            {
                _guildId = guildId;
                _textChannelId = textChannelId;
                _vc = vc;
                _ollama = ollama;
                _voiceService = voiceService;
                _restClient = restClient;
                _gatewayClient = gatewayClient;
                _whisper = whisper;
                _logger = logger;
            }

            public void Start()
            {
                _vc.Speaking += OnSpeaking;
                _vc.VoiceReceive += OnVoiceReceive;
            }

            private int _totalPacketsReceived = 0;

            private ValueTask OnSpeaking(SpeakingEventArgs args)
            {
                if (args.UserId != 0 && args.Ssrc != 0)
                {
                    _ssrcToUser[args.Ssrc] = args.UserId;
                    _logger.LogInformation("[VoiceInput] Speaking: SSRC={Ssrc} → UserId={UserId} flags={Flags}",
                        args.Ssrc, args.UserId, args.Speaking);
                }
                return ValueTask.CompletedTask;
            }

            private ValueTask OnVoiceReceive(VoiceReceiveEventArgs args)
            {
                if (_disposed) return ValueTask.CompletedTask;

                // Suppress while TTS is playing
                if (Environment.TickCount64 < Interlocked.Read(ref _ignoreUntilMs))
                    return ValueTask.CompletedTask;

                // Copy the span immediately — only valid during this call
                var packet = args.Frame.ToArray();
                if (packet.Length == 0) return ValueTask.CompletedTask;

                var ssrc = args.Ssrc;

                // Log first few packets to confirm the receive handler is working
                var count = System.Threading.Interlocked.Increment(ref _totalPacketsReceived);
                if (count <= 3 || count % 500 == 0)
                    _logger.LogInformation("[VoiceInput] Packet #{Count}: SSRC={Ssrc} len={Len}", count, ssrc, packet.Length);

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

                _logger.LogInformation(
                    "[VoiceInput] Utterance from SSRC={Ssrc} User={User} ({Count} packets)",
                    ssrc, username ?? userId.ToString(), packets.Length);

                // Serialise: wait for any current response to finish before starting this one
                await _responseQueue.WaitAsync();
                try
                {
                    // 1. Decode Opus → raw s16le PCM (48 kHz stereo)
                    byte[] rawPcm = DecodeOpusToPcm(packets);

                    // Skip very short clips (< ~200 ms)
                    if (rawPcm.Length < 19200)
                    {
                        _logger.LogDebug("[VoiceInput] Utterance too short ({Bytes} bytes PCM), skipping", rawPcm.Length);
                        return;
                    }

                    // 2. Resample to 16 kHz mono WAV via ffmpeg
                    byte[] wavBytes = await ResampleToWhisperWavAsync(rawPcm);
                    if (wavBytes.Length == 0)
                    {
                        _logger.LogWarning("[VoiceInput] ffmpeg produced no WAV output for SSRC={Ssrc}", ssrc);
                        return;
                    }

                    // 3. Transcribe
                    var transcript = await _whisper.TranscribeAsync(wavBytes);
                    if (string.IsNullOrWhiteSpace(transcript))
                    {
                        _logger.LogDebug("[VoiceInput] Empty transcript from SSRC={Ssrc}", ssrc);
                        return;
                    }

                    _logger.LogInformation("[VoiceInput] Transcript ({User}): \"{Text}\"",
                        username ?? "?", transcript);

                    // 4. Generate AI response — prefix with the speaker's name so Chizu
                    //    knows who to address
                    string messageForAi = username is not null
                        ? $"{username} says: {transcript}"
                        : transcript;

                    var response = await _ollama.GenerateAsync(
                        messageForAi, [], requireResponse: true, imageUrls: null);

                    if (response is null) return;

                    _logger.LogInformation("[VoiceInput] AI response ({Len} chars): {Snippet}",
                        response.Length,
                        response.Length > 120 ? response[..120] + "…" : response);

                    // 5. Post to text channel
                    try
                    {
                        string who = userId > 0 ? $"<@{userId}>" : (username ?? "someone");
                        await _restClient.SendMessageAsync(_textChannelId, new MessageProperties
                        {
                            Content = $"🎙️ **{who}**: {transcript}\n💬 **Chizu**: {response}"
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[VoiceInput] Failed to post transcript to channel");
                    }

                    // 6. TTS playback (awaited so _responseQueue holds until audio finishes)
                    await PlayTtsAsync(response);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[VoiceInput] Pipeline error for SSRC={Ssrc}", ssrc);
                }
                finally
                {
                    _responseQueue.Release();
                }
            }

            // -----------------------------------------------------------------
            // Username lookup — REST call on first encounter, cached after
            // -----------------------------------------------------------------

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
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[VoiceInput] Could not look up username for {UserId}", userId);
                    return null;
                }
            }

            // -----------------------------------------------------------------
            // Audio helpers
            // -----------------------------------------------------------------

            private byte[] DecodeOpusToPcm(byte[][] packets)
            {
                const int FrameSamplesPerChannel = 960; // 20 ms at 48 kHz
                const int Channels = 2;

                using var decoder = new OpusDecoder(VoiceChannels.Stereo);
                using var ms = new MemoryStream(packets.Length * FrameSamplesPerChannel * Channels * sizeof(short));

                var pcmShorts = new short[FrameSamplesPerChannel * Channels];
                var pcmBytes  = new byte[pcmShorts.Length * sizeof(short)];

                foreach (var packet in packets)
                {
                    try
                    {
                        int decoded = decoder.Decode(packet, pcmShorts.AsSpan(), FrameSamplesPerChannel);
                        int byteCount = decoded * Channels * sizeof(short);
                        Buffer.BlockCopy(pcmShorts, 0, pcmBytes, 0, byteCount);
                        ms.Write(pcmBytes, 0, byteCount);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[VoiceInput] OpusDecoder.Decode failed ({Len} bytes)", packet.Length);
                    }
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

            // -----------------------------------------------------------------
            // TTS synthesis: try edge-tts (natural neural voice), else SAPI female
            // -----------------------------------------------------------------

            private async Task PlayTtsAsync(string text)
            {
                try
                {
                    // Suppress incoming audio while TTS is in flight
                    long suppressMs = Math.Max(4000, text.Length * 90L);
                    Interlocked.Exchange(ref _ignoreUntilMs, Environment.TickCount64 + suppressMs);

                    // Try edge-tts first; fall back to SAPI on any failure
                    byte[]? audioBytes = await TryEdgeTtsAsync(text, EdgeTtsVoice);
                    byte[] pcmSourceBytes = audioBytes ?? SynthesizeSystemSpeechWav(text);

                    if (pcmSourceBytes.Length == 0)
                    {
                        _logger.LogWarning("[VoiceInput] TTS produced 0 bytes, skipping playback");
                        return;
                    }

                    var track = new Track("Chizu Voice Reply", TrackSourceType.StreamFactory)
                    {
                        StreamFactory = ct => Task.FromResult(OpenPcmFromAudioBytes(pcmSourceBytes, ct)),
                    };

                    await _voiceService.PlayNowAsync(_guildId, track);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[VoiceInput] TTS playback failed");
                }
                finally
                {
                    Interlocked.Exchange(ref _ignoreUntilMs, 0);
                }
            }

            /// <summary>
            /// Calls edge-tts (must be installed: pip install edge-tts) and returns
            /// the raw MP3 bytes, or null if edge-tts is unavailable or fails.
            /// </summary>
            private async Task<byte[]?> TryEdgeTtsAsync(string text, string voice)
            {
                try
                {
                    // edge-tts --voice en-US-JennyNeural --text "..." --write-media -
                    // Outputs MP3 data to stdout. ArgumentList handles all escaping.
                    var psi = new ProcessStartInfo("edge-tts")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = false,
                        CreateNoWindow = true,
                    };
                    psi.ArgumentList.Add("--voice");
                    psi.ArgumentList.Add(voice);
                    psi.ArgumentList.Add("--text");
                    psi.ArgumentList.Add(text);
                    psi.ArgumentList.Add("--write-media");
                    psi.ArgumentList.Add("-");

                    using var proc = new Process { StartInfo = psi };
                    proc.Start();

                    using var ms = new MemoryStream();
                    await proc.StandardOutput.BaseStream.CopyToAsync(ms);
                    await proc.WaitForExitAsync();

                    byte[] mp3 = ms.ToArray();

                    if (proc.ExitCode != 0 || mp3.Length < 512)
                    {
                        _logger.LogDebug("[VoiceInput] edge-tts returned exit={Code} len={Len}, falling back to SAPI",
                            proc.ExitCode, mp3.Length);
                        return null;
                    }

                    _logger.LogDebug("[VoiceInput] edge-tts produced {Len} bytes MP3", mp3.Length);
                    return mp3;
                }
                catch (Exception ex)
                {
                    // edge-tts not installed or not on PATH
                    _logger.LogDebug("[VoiceInput] edge-tts unavailable ({Msg}), using SAPI fallback", ex.Message);
                    return null;
                }
            }

            private static byte[] SynthesizeSystemSpeechWav(string text)
            {
                using var synth = new SpeechSynthesizer();
                synth.SelectVoiceByHints(VoiceGender.Female);
                synth.Rate = 1;
                using var ms = new MemoryStream();
                synth.SetOutputToWaveStream(ms);
                synth.Speak(text);
                synth.SetOutputToNull();
                return ms.ToArray();
            }

            /// <summary>
            /// Converts any audio format (WAV or MP3) to s16le 48 kHz stereo PCM
            /// via ffmpeg and returns the stdout stream for VoiceService to consume.
            /// </summary>
            private static Stream OpenPcmFromAudioBytes(byte[] audioBytes, CancellationToken ct)
            {
                var psi = new ProcessStartInfo("ffmpeg")
                {
                    Arguments = "-i pipe:0 -f s16le -ar 48000 -ac 2 pipe:1 -loglevel quiet",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };

                var proc = new Process { StartInfo = psi };
                proc.Start();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await proc.StandardInput.BaseStream.WriteAsync(audioBytes, ct);
                        proc.StandardInput.BaseStream.Close();
                    }
                    catch { /* cancelled or already closed */ }
                }, CancellationToken.None);

                return proc.StandardOutput.BaseStream;
            }

            public void Dispose()
            {
                _disposed = true;
                _responseQueue.Dispose();
                try { _vc.Speaking -= OnSpeaking; } catch { }
                try { _vc.VoiceReceive -= OnVoiceReceive; } catch { }
                foreach (var buf in _buffers.Values)
                    buf.Dispose();
                _buffers.Clear();
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

            // 800 ms gap = end of utterance; cap at ~12 s to avoid runaway buffers
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

                _logger.LogDebug("[VoiceInput] Silence for SSRC={Ssrc}, queuing utterance ({Count} packets)", _ssrc, captured.Length);
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
