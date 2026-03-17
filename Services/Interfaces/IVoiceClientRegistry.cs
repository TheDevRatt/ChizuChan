using NetCord.Gateway.Voice;

namespace ChizuChan.Services.Interfaces
{
    /// <summary>
    /// Stores the active VoiceClient per guild so that services and commands can share it
    /// (e.g. the music bot and the voice-input listener both need the same connection).
    /// </summary>
    public interface IVoiceClientRegistry
    {
        void Register(ulong guildId, VoiceClient voiceClient);
        bool TryGet(ulong guildId, out VoiceClient? voiceClient);
        void Unregister(ulong guildId);
    }
}
