using ChizuChan.Services.Interfaces;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Commands
{
    public class MusicCommandModule : ApplicationCommandModule<ApplicationCommandContext>
    {
        private IGuildService _guildService;
        

        //public MusicCommandModule(IGuildService guildService)
        //{
        //    _guildService = guildService;
        //}

        //[SlashCommand("play", "Play a YouTube Link, SoundClound Link or hell, try your fucking luck with some obscure ass website link I guess.", Contexts = [InteractionContextType.Guild])]
        //public async Task PlayAsync(string track)
        //{
        //    await RespondAsync(InteractionCallback.DeferredMessage());
        //}
    }
}
