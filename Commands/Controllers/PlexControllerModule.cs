using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Commands.Controllers
{
    public class PlexControllerModule : ComponentInteractionModule<ComponentInteractionContext>
    {

        private readonly IEmbedService _embedService;
        private readonly IPlexService _plexService;
        private readonly RestClient _restClient;
        private readonly ApiKeyOptions _apiKeys;

        public PlexControllerModule (IEmbedService embedService, RestClient restClient, IPlexService plexService, IOptions<ApiKeyOptions> apiKeyOptions)
        {
            _embedService = embedService;
            _restClient = restClient;
            _plexService = plexService;
            _apiKeys = apiKeyOptions.Value;
        }

        [ComponentInteraction("next_button")]
        public async Task NextResultAsync()
        {
            await HandleNavigationAsync(forward: true);
        }

        [ComponentInteraction("previous_button")]
        public async Task PreviousResultAsync()
        {
            await HandleNavigationAsync(forward: false);
        }

        [ComponentInteraction("select_button")]
        public async Task SelectResultAsync()
        {
            ulong userId = Context.User.Id;

            await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

            if (!PlexService.SearchResults.ContainsKey(userId))
            {
                await ModifyResponseAsync(message => message.Content = "No search results found for this session");
                return;
            }

            (LookupDTO Result, int CurrentPage, int CurrentIndex, ulong MessageId, string query) userData = PlexService.SearchResults[userId];
            LookupDTO resultData = userData.Result;
            int currentPage = userData.CurrentPage;
            int currentIndex = userData.CurrentIndex;
            ulong messageId = userData.MessageId;
            string query = userData.query;

            List<ResultDTO> results = resultData.Results;
            if (resultData == null || resultData.Results == null || resultData.Results.Count == 0)
            {
                await ModifyResponseAsync(message => message.Content = "No results to select.");
                return;
            }

            ResultDTO selectedResult = results[currentIndex];

            if (selectedResult.MediaInfo != null)
            {
                await ModifyResponseAsync(message => message.Content = $"Ay Buddy, This shows already been downloaded, check it out here:\n{selectedResult.MediaInfo.PlexUrl}");
                return;
            }

            ModalProperties modal = _embedService.BuildSearchModal(selectedResult, currentIndex, currentPage);

            await RespondAsync(InteractionCallback.Modal(modal));

        }

        #region Helper Methods
        private async Task HandleNavigationAsync(bool forward)
        {
            ulong userId = Context.User.Id;

            if (!PlexService.SearchResults.ContainsKey(userId))
            {
                await RespondAsync(InteractionCallback.Message("No search results found for this session."));
                return;
            }

            (LookupDTO Result, int CurrentPage, int CurrentIndex, ulong MessageId, string query) userData = PlexService.SearchResults[userId];
            LookupDTO resultData = userData.Result;
            int currentPage = userData.CurrentPage;
            int currentIndex = userData.CurrentIndex;
            ulong messageId = userData.MessageId;
            string query = userData.query;

            if (resultData == null || resultData.Results == null || resultData.Results.Count == 0)
            {
                await RespondAsync(InteractionCallback.Message("No valid results to display."));
                return;
            }

            List<ResultDTO> results = resultData.Results;
            int totalResults = results.Count;
            int newIndex = currentIndex;

            if (forward)
            {
                newIndex++;

                if (newIndex >= totalResults && currentPage < resultData.TotalPages)
                {
                    currentPage++;
                    LookupDTO? nextPageResult = await _plexService.GetSeriesInfoAsync(
                        query,
                        _apiKeys.OverseerrKey,
                        userId,
                        currentPage,
                        messageId
                    );

                    if (nextPageResult == null || nextPageResult.Results == null || nextPageResult.Results.Count == 0)
                    {
                        await RespondAsync(InteractionCallback.Message("No more results available."));
                        return;
                    }

                    resultData = nextPageResult;
                    results = resultData.Results;
                    newIndex = 0;
                }
                else if (newIndex >= totalResults)
                {
                    newIndex = 0;
                }
            }
            else
            {
                newIndex--;

                if (newIndex < 0 && currentPage > 1)
                {
                    currentPage--;
                    LookupDTO? prevPageResult = await _plexService.GetSeriesInfoAsync(
                        query,
                        _apiKeys.OverseerrKey,
                        userId,
                        currentPage,
                        messageId
                    );

                    if (prevPageResult == null || prevPageResult.Results == null || prevPageResult.Results.Count == 0)
                    {
                        await RespondAsync(InteractionCallback.Message("No previous results available."));
                        return;
                    }

                    resultData = prevPageResult;
                    results = resultData.Results;
                    newIndex = results.Count - 1;
                }
                else if (newIndex < 0)
                {
                    newIndex = totalResults - 1;
                }
            }

            ResultDTO selectedResult = results[newIndex];

            (EmbedProperties Embed, IComponentProperties[] Components) rebuilt = _embedService.BuildSearchEmbed(
                record: selectedResult,
                index: newIndex,
                total: results.Count,
                page: currentPage,
                totalPages: resultData.TotalPages
            );

            PlexService.SearchResults[userId] = (resultData, currentPage, newIndex, messageId, query);

            await RespondAsync(InteractionCallback.DeferredModifyMessage);

            await _restClient.ModifyMessageAsync(
                Context.Channel.Id,
                messageId,
                message =>
                {
                    message.Embeds = new EmbedProperties[] { rebuilt.Embed };
                    message.Components = rebuilt.Components;
                });
        }
        #endregion
    }
}
