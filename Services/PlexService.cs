using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ChizuChan.Services
{
    public class PlexService : IPlexService
    {
        private readonly HttpClient _httpClient;
        public static Dictionary<ulong, (LookupDTO Result, int CurrentPage, int CurrentIndex, ulong MessageId, string Query, bool RequestMade)> SearchResults { get; } = new();

        public PlexService(HttpClient httpClient, IOptions<ApiKeyOptions> apiKeyOptions)
        {
            _httpClient = httpClient;
            ApiKeyOptions keys = apiKeyOptions.Value;
        }

        public async Task<StandardResponse<LookupDTO>> GetSeriesInfoAsync(string query, string apiKey, ulong userId, int page = 1, ulong messageId = 0)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"http://192.168.2.211:5055/api/v1/search?query={query}&page={page}&language=en");
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
                    SearchResults[userId] = (new LookupDTO { Results = new List<ResultDTO>() }, page, 0, messageId, query, RequestMade: false);
                    return new StandardResponse<LookupDTO>
                    {
                        StatusCode = (int)HttpStatusCode.NotFound,
                        ErrorMessage = "No results found.",
                        Data = null
                    };
                }

                SearchResults[userId] = (result, page, 0, messageId, query, RequestMade: false);
                return new StandardResponse<LookupDTO>
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    ErrorMessage = null,
                    Data = result
                };
            }
            catch (HttpRequestException ex)
            {
                return new StandardResponse<LookupDTO>
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    ErrorMessage = $"HTTP Request error: {ex.Message}",
                    Data = null
                };
            }
            catch (JsonException ex)
            {
                return new StandardResponse<LookupDTO>
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    ErrorMessage = $"Deserialization error: {ex.Message}",
                    Data = null
                };
            }
            catch (Exception ex)
            {
                return new StandardResponse<LookupDTO>
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    ErrorMessage = $"Unexpected error: {ex.Message}",
                    Data = null
                };
            }

        }

        public async Task<StandardResponse<TvDetailsDTO>> GetTvDetailsAsync(int tvId, string apiKey, ulong userId)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"http://192.168.2.211:5055/api/v1/tv/{tvId}?language=en");
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

                TvDetailsDTO? result = JsonSerializer.Deserialize<TvDetailsDTO>(content, options);

                if (response.IsSuccessStatusCode && result != null)
                {
                    return StandardResponse<TvDetailsDTO>.SuccessResponse(result, (int)response.StatusCode);
                }
                else
                {
                    return StandardResponse<TvDetailsDTO>.ErrorResponse("Failed to retrieve valid series data.", (int)response.StatusCode);
                }
            }
            catch (HttpRequestException httpEx)
            {
                return StandardResponse<TvDetailsDTO>.ErrorResponse($"Network Error: {httpEx.Message}", 503);
            }
            catch (JsonException jsonEx)
            {
                return StandardResponse<TvDetailsDTO>.ErrorResponse($"Data format error: {jsonEx.Message}", 500);
            }
            catch (Exception ex)
            {
                return StandardResponse<TvDetailsDTO>.ErrorResponse($"Unexpected error: {ex.Message}", 500);
            }

        }

        public async Task<StandardResponse<MovieDetailsDTO>> GetMovieDetailsAsync(int movieId, string apiKey, ulong userId)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"http://192.168.2.211:5055/api/v1/movie/{movieId}?language=en");
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

                MovieDetailsDTO? result = JsonSerializer.Deserialize<MovieDetailsDTO>(content, options);

                if (response.IsSuccessStatusCode && result != null)
                {
                    return StandardResponse<MovieDetailsDTO>.SuccessResponse(result, (int)response.StatusCode);
                }
                else
                {
                    return StandardResponse<MovieDetailsDTO>.ErrorResponse("Failed to retrieve valid movie data.", (int)response.StatusCode);
                }
            }
            catch (HttpRequestException httpEx)
            {
                return StandardResponse<MovieDetailsDTO>.ErrorResponse($"Network Error: {httpEx.Message}", 503);
            }
            catch (JsonException jsonEx)
            {
                return StandardResponse<MovieDetailsDTO>.ErrorResponse($"Data format error: {jsonEx.Message}", 500);
            }
            catch (Exception ex)
            {
                return StandardResponse<MovieDetailsDTO>.ErrorResponse($"Unexpected error: {ex.Message}", 500);
            }
        }

        public async Task<StandardResponse<RequestMediaResultDTO>> RequestMediaAsync(RequestMediaDTO requestedMedia, string apiKey)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, $"http://192.168.2.211:5055/api/v1/request");
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("X-Api-Key", apiKey);

                JsonSerializerOptions options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                string jsonBody = JsonSerializer.Serialize(requestedMedia, options);

                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                string content = await response.Content.ReadAsStringAsync();

                RequestMediaResultDTO? result = JsonSerializer.Deserialize<RequestMediaResultDTO>(content, options);

                if (response.IsSuccessStatusCode && result != null)
                {
                    return StandardResponse<RequestMediaResultDTO>.SuccessResponse(result, (int)response.StatusCode);
                }
                else
                {
                    return StandardResponse<RequestMediaResultDTO>.ErrorResponse("Failed to retrieve valid movie data.", (int)response.StatusCode);
                }
            }
            catch (HttpRequestException httpEx)
            {
                return StandardResponse<RequestMediaResultDTO>.ErrorResponse($"Network Error: {httpEx.Message}", 503);
            }
            catch (JsonException jsonEx)
            {
                return StandardResponse<RequestMediaResultDTO>.ErrorResponse($"Data format error: {jsonEx.Message}", 500);
            }
            catch (Exception ex)
            {
                return StandardResponse<RequestMediaResultDTO>.ErrorResponse($"Unexpected error: {ex.Message}", 500);
            }

        }

        public string GetDownloadStatus(ResultDTO record)
        {
            if (record == null || record.MediaInfo == null)
            {
                return "❌ Untracked";
            }

            switch (record.MediaInfo.Status)
            {
                case 1:
                    return "❓ Unknown";
                case 2:
                    return "🔃 Pending";
                case 3:
                    return "⚙️ Processing";
                case 4:
                    return "📦 Partially Available";
                case 5:
                    return "✅ Available";
                default:
                    return "❌ Untracked";
            }
        }
    }
}
