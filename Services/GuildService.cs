using ChizuChan.Services.Interfaces;
using NetCord.Gateway;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Services
{
    public class GuildService : IGuildService
    {
        private readonly Dictionary<ulong, Guild> _guilds = new();

        public void AddOrUpdateGuild(Guild guild)
        {
            _guilds[guild.Id] = guild;
        }

        public Guild? GetGuild(ulong guildId)
        {
            if (_guilds.ContainsKey(guildId))
            {
                Guild? guild = _guilds[guildId];
                return guild;
            }
            else
            {
                return null;
            }
        }
    }
}
