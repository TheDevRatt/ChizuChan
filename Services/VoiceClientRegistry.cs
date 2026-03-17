using ChizuChan.Services.Interfaces;
using NetCord.Gateway.Voice;
using System.Collections.Concurrent;

namespace ChizuChan.Services
{
    public class VoiceClientRegistry : IVoiceClientRegistry
    {
        private readonly ConcurrentDictionary<ulong, VoiceClient> _clients = new();

        public void Register(ulong guildId, VoiceClient voiceClient)
            => _clients[guildId] = voiceClient;

        public bool TryGet(ulong guildId, out VoiceClient? voiceClient)
            => _clients.TryGetValue(guildId, out voiceClient);

        public void Unregister(ulong guildId)
            => _clients.TryRemove(guildId, out _);
    }
}
