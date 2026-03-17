using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChizuChan.Services
{
    public class OllamaService : IOllamaService
    {
        private const string IgnoreSentinel = "[IGNORE]";

        private readonly HttpClient _httpClient;
        private readonly OllamaOptions _options;
        private readonly IOllamaModelState _modelState;
        private readonly ILogger<OllamaService> _logger;

        public OllamaService(
            HttpClient httpClient,
            IOptions<OllamaOptions> options,
            IOllamaModelState modelState,
            ILogger<OllamaService> logger)
        {
            _httpClient = httpClient;
            _options    = options.Value;
            _modelState = modelState;
            _logger     = logger;
        }

        public async Task<string?> GenerateAsync(
            string userMessage,
            IList<(string Author, string Content, bool IsBot)> contextMessages,
            bool requireResponse,
            IList<string>? imageUrls = null)
        {
            var currentModel = _modelState.CurrentModel;

            // Download and base64-encode any images up front
            var base64Images = new List<string>();
            if (imageUrls is { Count: > 0 } && _modelState.IsVisionModel)
            {
                foreach (var url in imageUrls)
                {
                    try
                    {
                        var bytes = await _httpClient.GetByteArrayAsync(url);
                        base64Images.Add(Convert.ToBase64String(bytes));
                        _logger.LogInformation("[Ollama] Downloaded image {Url} ({Bytes} bytes)", url, bytes.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Ollama] Failed to download image {Url}", url);
                    }
                }
            }

            // ── System message ────────────────────────────────────────────────
            var systemContent = new StringBuilder(_options.SystemPrompt);

            if (contextMessages.Count > 0)
            {
                systemContent.Append("\n\nRecent conversation context (oldest → newest):\n");
                foreach (var (author, content, isBot) in contextMessages)
                    systemContent.Append($"{(isBot ? "Chizu" : author)}: {content}\n");
            }

            if (!requireResponse)
            {
                systemContent.Append(
                    $"\n\nIMPORTANT: Your name was mentioned in the next message, but you must " +
                    $"decide whether the user is directly speaking to you (asking a question, " +
                    $"giving a command, greeting you) or merely talking ABOUT you to someone else. " +
                    $"If they are NOT directly addressing you, respond with exactly: {IgnoreSentinel} " +
                    $"and nothing else. Otherwise reply normally.");
            }

            // ── User message — attach base64 images if present ────────────────
            var userMsg = new JsonObject
            {
                ["role"]    = "user",
                ["content"] = userMessage
            };

            if (base64Images.Count > 0)
            {
                var imagesNode = new JsonArray();
                foreach (var b64 in base64Images)
                    imagesNode.Add(b64);
                userMsg["images"] = imagesNode;
            }

            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemContent.ToString() },
                userMsg
            };

            var requestBody = new JsonObject
            {
                ["model"]    = currentModel,
                ["stream"]   = false,
                ["messages"] = messages
            }.ToJsonString();

            // ── Diagnostics ───────────────────────────────────────────────────
            _logger.LogInformation("[Ollama] URL:   {Url}", _options.BaseUrl);
            _logger.LogInformation("[Ollama] Model: {Model}", currentModel);
            _logger.LogInformation("[Ollama] Token present: {HasToken}", !string.IsNullOrEmpty(_options.BearerToken));
            _logger.LogInformation("[Ollama] Images attached: {Count}", base64Images.Count);
            _logger.LogInformation("[Ollama] System prompt ({Len} chars): {Snippet}",
                systemContent.Length,
                systemContent.Length > 200 ? systemContent.ToString()[..200] + "…" : systemContent.ToString());
            _logger.LogInformation("[Ollama] User message: {Msg}", userMessage);
            _logger.LogInformation("[Ollama] Context messages: {Count}", contextMessages.Count);
            _logger.LogDebug("[Ollama] Full request body: {Body}", requestBody);

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);

            _logger.LogInformation("[Ollama] Response status: {Status}", (int)response.StatusCode);

            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[Ollama] Error response body: {Body}", json);
                response.EnsureSuccessStatusCode();
            }

            _logger.LogDebug("[Ollama] Raw response: {Json}", json);

            using var doc = JsonDocument.Parse(json);

            var text = doc.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
                ?.Trim()
                ?? "I couldn't generate a response.";

            _logger.LogInformation("[Ollama] Model reply ({Len} chars): {Snippet}",
                text.Length,
                text.Length > 200 ? text[..200] + "…" : text);

            if (!requireResponse && text.StartsWith(IgnoreSentinel, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[Ollama] Model chose to ignore (returned sentinel)");
                return null;
            }

            return text.Length > 1900 ? text[..1900] + "…" : text;
        }
    }
}
