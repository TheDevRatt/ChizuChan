using NetCord.Gateway;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Services.Interfaces
{
    public interface IGuildService
    {
        void AddOrUpdateGuild(Guild guild);
        Guild? GetGuild(ulong guildId);
    }
}
