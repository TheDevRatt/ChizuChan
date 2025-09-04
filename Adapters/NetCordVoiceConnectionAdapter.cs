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

            // Optional: advertise speaking before sending audio. This exists on VoiceClient.
            await _voiceClient.EnterSpeakingStateAsync(SpeakingFlags.Microphone);

            Stream rtpOut = _voiceClient.CreateOutputStream();

            // Encode PCM -> Opus and write to VoiceClient's RTP stream.
            OpusEncodeStream opus = new OpusEncodeStream(
                rtpOut,
                PcmFormat.Short,          // s16le
                VoiceChannels.Stereo,
                OpusApplication.Audio);

            _pcmSink = opus;
            return opus;
        }

        public Task StopSpeakingAsync()
        {
            if (_disposed)
                return Task.CompletedTask;

            // Sending a speaking flag of 0 tells Discord the bot has stopped talking.
            return _voiceClient.EnterSpeakingStateAsync(0).AsTask();
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
