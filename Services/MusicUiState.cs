using ChizuChan.Services.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Services
{
    public sealed class MusicUiState : IMusicUiState
    {
        private readonly ConcurrentDictionary<ulong, (ulong ChannelId, ulong MessageId)> _map = new();

        public void SetNowPlayingMessage(ulong guildId, ulong channelId, ulong messageId)
            => _map[guildId] = (channelId, messageId);

        public bool TryGetNowPlayingMessage(ulong guildId, out (ulong ChannelId, ulong MessageId) reference)
            => _map.TryGetValue(guildId, out reference);

        public void ClearNowPlayingMessage(ulong guildId)
            => _map.TryRemove(guildId, out _);
    }
}
