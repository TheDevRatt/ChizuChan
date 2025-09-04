using ChizuChan.Services.Interfaces;
using NetCord.Gateway;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Services
{
    public class GuildService : IGuildService
    {
        private readonly ConcurrentDictionary<ulong, Guild> _guilds = new();

        public void AddOrUpdateGuild(Guild guild)
            => _guilds[guild.Id] = guild;

        public Guild? GetGuild(ulong guildId)
            => _guilds.TryGetValue(guildId, out var g) ? g : null;
    }
}
