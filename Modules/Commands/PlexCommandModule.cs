using ChizuChan.DTOs;
using ChizuChan.Services.Interfaces;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Modules.Commands
{
    public class PlexCommandModule : ApplicationCommandModule<ApplicationCommandContext>
    {
        private readonly IGuildService _guildService;

        public PlexCommandModule(IGuildService guildService)
        {
            _guildService = guildService;
        }

        [SlashCommand("search", "Search for a Movie, TV Series, Anime, or a Person!", Contexts = [InteractionContextType.BotDMChannel])]
        public async Task SearchAsync(string query)
        {
            await RespondAsync(InteractionCallback.DeferredMessage());
            
            if (Context.Guild != null)
            {
                await ModifyResponseAsync(message => message.Content = "This command can only be used in DMs.");
                return;
            }

            if (string.IsNullOrEmpty(query))
            {
                await ModifyResponseAsync(message => message.Content = "Honestly, I don't even know how you managed to run this command with no search terms, but I added a check for that so fuck you <3");
                return;
            }


        }
    }
}
