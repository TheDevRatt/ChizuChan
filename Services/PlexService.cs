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

        public PlexService(HttpClient httpClient, IOptions<ApiKeyOptions> apiKeyOptions)
        {
            _httpClient = httpClient;
            ApiKeyOptions keys = apiKeyOptions.Value;
        }

        public async Task<List<LookupDTO>> GetSeriesInfoAsync(string query, string apiKey)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"http://192.168.2.211:5055/api/v1/search?query={query}&page=1&language=en");
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

            List<LookupDTO>? result = JsonSerializer.Deserialize<List<LookupDTO>>(content, options);

            if (result == null || result.Count == 0)
            {
                return new List<LookupDTO>();
            }

            return result;
        }
    }
}
