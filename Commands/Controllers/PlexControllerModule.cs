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

            await RespondAsync(InteractionCallback.DeferredMessage());

            if (!PlexService.SearchResults.ContainsKey(userId))
            {
                await ModifyResponseAsync(message => message.Content = "No search results found for this session");
                return;
            }

            (LookupDTO Result, int CurrentPage, int CurrentIndex, ulong MessageId, string Query, bool RequestMade) userData = PlexService.SearchResults[userId];
            LookupDTO resultData = userData.Result;
            int currentPage = userData.CurrentPage;
            int currentIndex = userData.CurrentIndex;
            ulong messageId = userData.MessageId;
            string query = userData.Query;
            bool requestMade = userData.RequestMade;

            List<ResultDTO> results = resultData.Results;
            if (resultData == null || resultData.Results == null || resultData.Results.Count == 0)
            {
                await ModifyResponseAsync(message => message.Content = "No results to select.");
                return;
            }

            ResultDTO selectedResult = results[currentIndex];
            int selectedResultId = results[currentIndex].Id;

            if (selectedResult.MediaInfo != null)
            {
                await ModifyResponseAsync(message => message.Content = $"Ay Buddy, This shows already been downloaded, check it out here:\n{selectedResult.MediaInfo.PlexUrl ?? "*No link available, the shows probably downloading*"}");
                return;
            }

            if (userData.RequestMade)
            {
                await ModifyResponseAsync(message => message.Content = "You already requested this, BOZO.");
                return;
            }


            if (selectedResult.MediaType == "tv")
            {
                StandardResponse<TvDetailsDTO> tvResponse = await _plexService.GetTvDetailsAsync(selectedResultId, _apiKeys.OverseerrKey, userId);

                if (tvResponse.Data == null)
                {
                    await ModifyResponseAsync(message => message.Content = $"Failed to fetch TV details: {tvResponse.ErrorMessage ?? "Unknown error."}");
                    return;
                }

                TvDetailsDTO tvDetails = tvResponse.Data;

                if (tvDetails != null)
                {
                    bool isAnime = tvDetails.Keywords.Any(keyword => keyword.Name != null && keyword.Name.ToLower().Contains("anime"));
                    List<int> seasons = tvDetails.Seasons.Where(season => season.SeasonNumber > 0).Select(season => season.SeasonNumber).ToList();

                    if (isAnime)
                    {
                        try
                        {
                            RequestMediaDTO request = new RequestMediaDTO
                            {
                                MediaType = selectedResult.MediaType,
                                MediaId = selectedResultId,
                                TvdbId = tvDetails.ExternalIds.TvdbId,
                                Seasons = seasons,
                                Is4K = false,
                                ServerId = 1,
                                ProfileId = 7,
                                RootFolder = "/data/media/anime",
                                LanguageProfileId = 1,
                                UserId = 1
                            };

                            StandardResponse<RequestMediaResultDTO> result = await _plexService.RequestMediaAsync(request, _apiKeys.OverseerrKey);

                            if (result.Data == null)
                            {
                                await ModifyResponseAsync(message => message.Content = $"Something went wrong trying to request your show: {result.ErrorMessage ?? "Unknown error."}");
                                return;
                            }
                            else if (result != null)
                            {
                                PlexService.SearchResults[userId] = (resultData, currentPage, currentIndex, messageId, query, true);
                                await ModifyResponseAsync(message => message.Content = $"'Kay, I put **{selectedResult.Name ?? selectedResult.Title}** in the queue to be downloaded.");
                            }
                            else
                            {
                                await ModifyResponseAsync(message => message.Content = $"Honestly vro, not even sure what happened, but it fucked up.");
                            }
                        }
                        catch (Exception ex)
                        {
                            await ModifyResponseAsync(message => message.Content = $"Honestly chief, some shii went terribly wrong. Give Blinky a DM and let him know that requests are getting exception errors. Better yet, just show him this:\n{ex.Message}");
                            return;
                        }
                    }
                    else
                    {
                        try
                        {
                            RequestMediaDTO request = new RequestMediaDTO
                            {
                                MediaType = selectedResult.MediaType,
                                MediaId = selectedResultId,
                                TvdbId = tvDetails.ExternalIds.TvdbId,
                                Seasons = seasons,
                                Is4K = false,
                                ServerId = 0,
                                ProfileId = 8,
                                RootFolder = "/data/media/tv",
                                LanguageProfileId = 1,
                                UserId = 1
                            };


                            StandardResponse<RequestMediaResultDTO> result = await _plexService.RequestMediaAsync(request, _apiKeys.OverseerrKey);

                            if (result.Data == null)
                            {
                                await ModifyResponseAsync(message => message.Content = $"Something went wrong trying to request your show: {result.ErrorMessage ?? "Unknown error."}");
                                return;
                            }
                            else if (result != null)
                            {
                                PlexService.SearchResults[userId] = (resultData, currentPage, currentIndex, messageId, query, true);
                                await ModifyResponseAsync(message => message.Content = $"'Kay, I put **{selectedResult.Name ?? selectedResult.Title}** in the queue to be downloaded.");
                            }
                            else
                            {
                                await ModifyResponseAsync(message => message.Content = $"Honestly vro, not even sure what happened, but it fucked up.");
                            }
                        }
                        catch (Exception ex)
                        {
                            await ModifyResponseAsync(message => message.Content = $"Honestly chief, some shii went terribly wrong. Give Blinky a DM and let him know that requests are getting exception errors. Better yet, just show him this:\n{ex.Message}");
                            return;
                        }
                    }
                }
            }
            else
            {
                StandardResponse<MovieDetailsDTO> movieResponse = await _plexService.GetMovieDetailsAsync(selectedResultId, _apiKeys.OverseerrKey, userId);

                if (movieResponse.Data == null)
                {
                    await ModifyResponseAsync(message => message.Content = $"Failed to fetch Movie details: {movieResponse.ErrorMessage ?? "Unknown error."}");
                    return;
                }

                MovieDetailsDTO movieDetails = movieResponse.Data;

                if (movieDetails != null)
                {
                    bool isAnime = movieDetails.Keywords.Any(keyword => keyword.Name != null && keyword.Name.ToLower().Contains("anime"));

                    if (isAnime)
                    {
                        try
                        {
                            RequestMediaDTO request = new RequestMediaDTO
                            {
                                MediaType = selectedResult.MediaType,
                                MediaId = selectedResultId,
                                TvdbId = movieDetails.ExternalIds.TvdbId,
                                Is4K = false,
                                ServerId = 0,
                                ProfileId = 8,
                                RootFolder = "/data/media/movies",
                                UserId = 1,
                                Tags = new List<int>()
                            };

                            StandardResponse<RequestMediaResultDTO> result = await _plexService.RequestMediaAsync(request, _apiKeys.OverseerrKey);

                            if (result.Data == null)
                            {
                                await ModifyResponseAsync(message => message.Content = $"Something went wrong trying to request your show: {result.ErrorMessage ?? "Unknown error."}");
                                return;
                            }
                            else if (result != null)
                            {
                                PlexService.SearchResults[userId] = (resultData, currentPage, currentIndex, messageId, query, true);
                                await ModifyResponseAsync(message => message.Content = $"'Kay, I put **{selectedResult.Name ?? selectedResult.Title}** in the queue to be downloaded.");
                            }
                            else
                            {
                                await ModifyResponseAsync(message => message.Content = $"Honestly vro, not even sure what happened, but it fucked up.");
                            }
                        }
                        catch (Exception ex)
                        {
                            await ModifyResponseAsync(message => message.Content = $"Honestly chief, some shii went terribly wrong. Give Blinky a DM and let him know that requests are getting exception errors. Better yet, just show him this:\n{ex.Message}");
                            return;
                        }
                    }
                    else
                    {
                        try
                        {
                            RequestMediaDTO request = new RequestMediaDTO
                            {
                                MediaType = selectedResult.MediaType,
                                MediaId = selectedResultId,
                                TvdbId = movieDetails.ExternalIds.TvdbId,
                                Is4K = false,
                                ServerId = 0,
                                ProfileId = 8,
                                RootFolder = "/data/media/movies",
                                UserId = 1
                            };


                            StandardResponse<RequestMediaResultDTO> result = await _plexService.RequestMediaAsync(request, _apiKeys.OverseerrKey);

                            if (result.Data == null)
                            {
                                await ModifyResponseAsync(message => message.Content = $"Something went wrong trying to request your show: {result.ErrorMessage ?? "Unknown error."}");
                                return;
                            }
                            else if (result != null)
                            {
                                PlexService.SearchResults[userId] = (resultData, currentPage, currentIndex, messageId, query, true);
                                await ModifyResponseAsync(message => message.Content = $"'Kay, I put **{selectedResult.Name ?? selectedResult.Title}** in the queue to be downloaded.");
                            }
                            else
                            {
                                await ModifyResponseAsync(message => message.Content = $"Honestly vro, not even sure what happened, but it fucked up.");
                            }
                        }
                        catch (Exception ex)
                        {
                            await ModifyResponseAsync(message => message.Content = $"Honestly chief, some shii went terribly wrong. Give Blinky a DM and let him know that requests are getting exception errors. Better yet, just show him this:\n{ex.Message}");
                            return;
                        }
                    }
                }
            }
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

            (LookupDTO Result, int CurrentPage, int CurrentIndex, ulong MessageId, string Query, bool RequestMade) userData = PlexService.SearchResults[userId];
            LookupDTO resultData = userData.Result;
            int currentPage = userData.CurrentPage;
            int currentIndex = userData.CurrentIndex;
            ulong messageId = userData.MessageId;
            string query = userData.Query;
            bool requestMade = userData.RequestMade;

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
                    StandardResponse<LookupDTO> nextPage = await _plexService.GetSeriesInfoAsync(
                        query,
                        _apiKeys.OverseerrKey,
                        userId,
                        currentPage,
                        messageId
                    );

                    if (nextPage.Data == null)
                    {
                        await ModifyResponseAsync(message => message.Content = $"There is no content left on the next page.");
                        return;
                    }

                    LookupDTO nextPageResult = nextPage.Data;

                    if (nextPageResult == null || nextPageResult.Results == null || nextPageResult.Results.Count == 0)
                    {
                        await RespondAsync(InteractionCallback.Message("No more results available."));
                        return;
                    }

                    resultData = nextPageResult;
                    results = resultData.Results;
                    newIndex = 0;
                    requestMade = false;
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
                    StandardResponse<LookupDTO> prevPage = await _plexService.GetSeriesInfoAsync(
                        query,
                        _apiKeys.OverseerrKey,
                        userId,
                        currentPage,
                        messageId
                    );

                    if (prevPage.Data == null)
                    {
                        await ModifyResponseAsync(message => message.Content = $"There is no content on the previous page.");
                        return;
                    }

                    LookupDTO prevPageResult = prevPage.Data;

                    if (prevPageResult == null || prevPageResult.Results == null || prevPageResult.Results.Count == 0)
                    {
                        await RespondAsync(InteractionCallback.Message("No previous results available."));
                        return;
                    }

                    resultData = prevPageResult;
                    results = resultData.Results;
                    newIndex = results.Count - 1;
                    requestMade = false;
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

            PlexService.SearchResults[userId] = (resultData, currentPage, newIndex, messageId, query, requestMade);

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
