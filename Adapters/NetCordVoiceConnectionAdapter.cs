using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;
using NetCord.Gateway.Voice;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using static ChizuChan.Services.Interfaces.Track;

namespace ChizuChan.Adapters
{
    /// <summary>
    /// Accepts PCM s16le/48k/stereo from VoiceService and sends it via OpusEncodeStream over NetCord VoiceClient.
    /// Voice channel "leaving" (gateway voice state update) should be handled by your command/service layer.
    /// </summary>
    public sealed class NetCordVoiceConnectionAdapter : IVoiceConnectionAdapter
    {
        private readonly VoiceClient _voiceClient;
        private readonly ILogger _logger;

        private Stream? _pcmSink;
        private bool _disposed;

        public NetCordVoiceConnectionAdapter(VoiceClient voiceClient, ILogger logger)
        {
            _voiceClient = voiceClient;
            _logger = logger;
        }

        // NetCord doesn't expose a simple "IsConnected" on all versions; we track disposal here.
        public bool IsConnected => !_disposed;

        public async Task<Stream> OpenPcmSinkAsync(CancellationToken ct)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(NetCordVoiceConnectionAdapter));

            // The voice WebSocket handshake (UDP IP discovery + session description) may still
            // be in progress when this is called. Poll until the connection accepts payloads,
            // up to 15 seconds.
            const int maxAttempts = 75;   // 75 × 200 ms = 15 s
            const int delayMs = 200;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    _logger.LogInformation("[Adapter] OpenPcmSinkAsync: attempt {Attempt}/{Max} – calling EnterSpeakingStateAsync...", attempt, maxAttempts);
                    await _voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));
                    _logger.LogInformation("[Adapter] OpenPcmSinkAsync: EnterSpeakingStateAsync succeeded on attempt {Attempt}.", attempt);
                    break;  // success – exit poll loop
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("not started") || ex.Message.Contains("not connected"))
                {
                    _logger.LogInformation("[Adapter] OpenPcmSinkAsync: voice WS not ready yet (attempt {Attempt}), retrying in {Delay} ms...", attempt, delayMs);
                    if (attempt == maxAttempts)
                        throw new TimeoutException("Voice WebSocket did not become ready within 15 seconds.", ex);
                    await Task.Delay(delayMs, ct);
                }
            }

            _logger.LogInformation("[Adapter] OpenPcmSinkAsync: calling CreateVoiceStream...");
            Stream rtpOut = _voiceClient.CreateVoiceStream();
            _logger.LogInformation("[Adapter] OpenPcmSinkAsync: CreateVoiceStream done, wrapping with OpusEncodeStream...");

            OpusEncodeStream opus = new OpusEncodeStream(
                rtpOut,
                PcmFormat.Short,          // s16le
                VoiceChannels.Stereo,
                OpusApplication.Audio);

            _logger.LogInformation("[Adapter] OpenPcmSinkAsync: sink ready.");
            _pcmSink = opus;
            return opus;
        }

        public Task StopSpeakingAsync()
        {
            if (_disposed)
                return Task.CompletedTask;

            // Sending a speaking flag of 0 tells Discord the bot has stopped talking.
            return _voiceClient.EnterSpeakingStateAsync(new SpeakingProperties((SpeakingFlags)0)).AsTask();
        }

        public Task DisconnectAsync(CancellationToken ct)
        {
            if (_disposed) return Task.CompletedTask;

            try { _pcmSink?.Dispose(); } catch { }
            _pcmSink = null;

            try { _voiceClient.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "VoiceClient.Dispose() failed."); }

            _disposed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _ = DisconnectAsync(CancellationToken.None);
            return ValueTask.CompletedTask;
        }
    }
}
