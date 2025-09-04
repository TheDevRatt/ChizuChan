using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Services.Interfaces
{
    public interface IMusicUiState
    {
        void SetNowPlayingMessage(ulong guildId, ulong channelId, ulong messageId);
        bool TryGetNowPlayingMessage(ulong guildId, out (ulong ChannelId, ulong MessageId) reference);
        void ClearNowPlayingMessage(ulong guildId);
    }
}
