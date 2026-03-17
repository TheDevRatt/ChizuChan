using NetCord.Gateway.Voice;

namespace ChizuChan.Services.Interfaces
{
    /// <summary>
    /// Manages per-guild voice-input listeners that pipe user speech through
    /// STT → Ollama AI → TTS and play the response back in the voice channel.
    /// </summary>
    public interface IVoiceInputService
    {
        /// <summary>
        /// Begins listening to <paramref name="voiceClient"/> for the given guild.
        /// Text responses are posted to <paramref name="textChannelId"/>.
        /// </summary>
        void StartListening(ulong guildId, ulong textChannelId, VoiceClient voiceClient);

        bool IsListening(ulong guildId);

        void StopListening(ulong guildId);
    }
}
