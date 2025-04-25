using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Modules
{
    public class PlexCommandModule : ApplicationCommandModule<ApplicationCommandContext>
    {
        private readonly IPlexService _plexService;
        private readonly IGuildService _guildService;
        private readonly IEmbedService _embedService;
        private readonly ApiKeyOptions _apiKeys;

        public PlexCommandModule(IGuildService guildService, IPlexService plexService, IEmbedService embedService, IOptions<ApiKeyOptions> apiKeyOptions)
        {
            _guildService = guildService;
            _plexService = plexService;
            _embedService = embedService;
            _apiKeys = apiKeyOptions.Value;
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

            ulong userId = Context.User.Id;
            int page = 1;
            string apiKey = _apiKeys.OverseerrKey;
            LookupDTO? result = await _plexService.GetSeriesInfoAsync(query, apiKey, userId, page);

            if (result?.Results == null || result.Results.Count == 0)
            {
                await ModifyResponseAsync(message => message.Content = $"Sorry. No results were found for **{query}**");
                return;
            }

            ResultDTO firstResult = result.Results[0];
            int index = 0;
            int total = result.Results.Count;
            int totalPages = result.TotalPages;

            (EmbedProperties embed, IComponentProperties[] components) = _embedService.BuildSearchEmbed(
                record: firstResult,
                index: index,
                total: total,
                page: page,
                totalPages: totalPages
            );


            //var embed = new EmbedProperties
            //{
            //    Title = "Test Result",
            //    Description = "This is a test description to ensure the embed renders correctly.",
            //    Fields = new[]
            //    {
            //        new EmbedFieldProperties
            //        {
            //            Name = "Test Field",
            //            Value = "Valid content here",
            //            Inline = false
            //        }
            //    },
            //    Footer = new EmbedFooterProperties
            //    {
            //        Text = "Result 1 of 1 • Page 1 of 1 • TMDB ID: 12345"
            //    }
            //};

            try
            {
                RestMessage responseMessage = await ModifyResponseAsync(message =>
                {
                    message.Embeds = new[] { embed };
                    message.Components = components;
                });

                ulong messageId = responseMessage.Id;

                // Store the messageId along with other relevant data
                PlexService.SearchResults[userId] = (result, page, index, messageId, query);
            }
            catch (RestException ex)
            {
                Console.WriteLine("Discord Error: " + ex.Message);
                Console.WriteLine("Exception: " + ex.GetType().Name);
                Console.WriteLine("Message: " + ex.Message);
                Console.WriteLine("Full dump: " + ex.ToString());
                await ModifyResponseAsync(message => message.Content = "Something went wrong, sorry there buddy.");
                throw;
            }
        }
    }
}
