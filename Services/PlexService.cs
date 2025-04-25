using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChizuChan.Services
{
    public class PlexService : IPlexService
    {
        private readonly HttpClient _httpClient;
        public static Dictionary<ulong, (LookupDTO Result, int CurrentPage, int CurrentIndex, ulong MessageId, string query)> SearchResults { get; } = new();

        public PlexService(HttpClient httpClient, IOptions<ApiKeyOptions> apiKeyOptions)
        {
            _httpClient = httpClient;
            ApiKeyOptions keys = apiKeyOptions.Value;
        }

        public async Task<LookupDTO?> GetSeriesInfoAsync(string query, string apiKey, ulong userId, int page = 1, ulong messageId = 0)
        {
            HttpRequestMessage request = new HttpRequestMessage(
                HttpMethod.Get,
                $"http://192.168.2.211:5055/api/v1/search?query={query}&page={page}&language=en"
            );
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("X-Api-Key", apiKey);

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync();
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };

            LookupDTO? result = JsonSerializer.Deserialize<LookupDTO>(content, options);

            if (result == null || result.Results == null || result.Results.Count == 0)
            {
                SearchResults[userId] = (new LookupDTO { Results = new List<ResultDTO>() }, page, 0, messageId, query);
                return result;
            }

            SearchResults[userId] = (result, page, 0, messageId, query);
            return result;
        }

        public string GetDownloadStatus(ResultDTO record)
        {
            if (record?.MediaInfo == null || record.MediaInfo.Seasons == null || record.MediaInfo.Seasons.Count == 0)
                return "❌ Untracked";

            List<SeasonDTO> seasons = record.MediaInfo.Seasons;

            bool allSeasonsDownloaded = seasons.All(season =>
                season.Status == 5 &&
                season.EpisodeCount > 0 &&
                season.EpisodeFileCount == season.EpisodeCount);

            bool someSeasonsIncomplete = seasons.Any(season =>
                season.EpisodeCount > 0 &&
                season.EpisodeFileCount < season.EpisodeCount);

            if (allSeasonsDownloaded)
                return "✅ Fully Downloaded";

            if (someSeasonsIncomplete)
                return "📦 Partially Downloaded";

            return "🔎 Monitored";
        }
    }
}
