using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
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
        private readonly LlmUsageTracker _usageTracker;
        private readonly LlmProviderOverrideState _overrideState;
        private readonly ILogger<OllamaService> _logger;

        public OllamaService(
            HttpClient httpClient,
            IOptions<OllamaOptions> options,
            IOllamaModelState modelState,
            LlmUsageTracker usageTracker,
            LlmProviderOverrideState overrideState,
            ILogger<OllamaService> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _modelState = modelState;
            _usageTracker = usageTracker;
            _overrideState = overrideState;
            _usageTracker.UseStore(_options.UsageStorePath);
            _overrideState.UseStore(_options.OverrideStorePath);
            _logger = logger;
        }

        public async Task<string?> GenerateAsync(
            string userMessage,
            IList<(string Author, string Content, bool IsBot)> contextMessages,
            bool requireResponse,
            IList<string>? imageUrls = null)
        {
            var providers = GetProviders().ToList();
            if (providers.Count == 0)
            {
                _logger.LogError("[LLM] No enabled LLM providers are configured.");
                return "I don't have any LLM providers configured.";
            }

            var base64Images = await DownloadImagesAsync(imageUrls, providers.Any(p => p.SupportsVision));
            var systemContent = BuildSystemPrompt(contextMessages, requireResponse);

            foreach (var provider in providers)
            {
                if (!_usageTracker.CanUse(provider))
                {
                    _logger.LogInformation("[LLM] Skipping provider {Provider}; local quota/cooldown says it is unavailable", provider.EffectiveName);
                    continue;
                }

                if (provider.Kind == LlmProviderKind.OpenAICompatible && string.IsNullOrWhiteSpace(provider.ResolveApiKey()))
                {
                    _logger.LogInformation("[LLM] Skipping provider {Provider}; no API key configured", provider.EffectiveName);
                    continue;
                }

                if (base64Images.Count > 0 && !provider.SupportsVision)
                {
                    _logger.LogInformation("[LLM] Skipping provider {Provider}; request has images but provider is not vision-capable", provider.EffectiveName);
                    continue;
                }

                try
                {
                    _logger.LogInformation("[LLM] Trying provider {Provider} ({Kind}) model {Model}", provider.EffectiveName, provider.Kind, provider.Model);
                    var result = provider.Kind switch
                    {
                        LlmProviderKind.Ollama => await SendOllamaAsync(provider, systemContent, userMessage, base64Images),
                        LlmProviderKind.OpenAICompatible => await SendOpenAICompatibleAsync(provider, systemContent, userMessage, base64Images),
                        _ => throw new InvalidOperationException($"Unsupported LLM provider kind: {provider.Kind}")
                    };

                    _usageTracker.RecordSuccess(provider, result.PromptTokens, result.CompletionTokens);
                    var text = result.Text.Trim();

                    _logger.LogInformation("[LLM] Provider {Provider} reply ({Len} chars): {Snippet}",
                        provider.EffectiveName,
                        text.Length,
                        text.Length > 200 ? text[..200] + "…" : text);

                    if (!requireResponse && text.StartsWith(IgnoreSentinel, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("[LLM] Model chose to ignore (returned sentinel)");
                        return null;
                    }

                    return text.Length > 1900 ? text[..1900] + "…" : text;
                }
                catch (LlmProviderRateLimitedException ex)
                {
                    _usageTracker.RecordRateLimit(provider);
                    _logger.LogWarning(ex, "[LLM] Provider {Provider} rate-limited; falling back", provider.EffectiveName);
                }
                catch (Exception ex)
                {
                    _usageTracker.RecordError(provider);
                    _logger.LogWarning(ex, "[LLM] Provider {Provider} failed; falling back", provider.EffectiveName);
                }
            }

            _logger.LogError("[LLM] All configured providers failed or were exhausted.");
            return "all my braincells are rate-limited right now. try again in a bit.";
        }

        private IEnumerable<LlmProviderOptions> GetProviders()
        {
            var providers = _options.Providers.Count > 0
                ? _options.Providers.Where(p => p.Enabled).ToList()
                :
                [
                    new LlmProviderOptions
                    {
                        Name = "local-ollama",
                        Kind = LlmProviderKind.Ollama,
                        BaseUrl = _options.BaseUrl,
                        ApiKey = _options.BearerToken,
                        Model = _modelState.CurrentModel,
                        SupportsVision = _modelState.IsVisionModel,
                        Priority = 100
                    }
                ];

            var overrideName = _overrideState.OverrideProviderName;
            if (!string.IsNullOrWhiteSpace(overrideName))
            {
                var match = providers.FirstOrDefault(p =>
                    string.Equals(p.EffectiveName, overrideName, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    _logger.LogInformation("[LLM] Provider override active: {Provider}", match.EffectiveName);
                    return providers
                        .OrderByDescending(p => ReferenceEquals(p, match))
                        .ThenBy(p => p.Priority)
                        .ThenBy(p => p.EffectiveName, StringComparer.OrdinalIgnoreCase);
                }

                _logger.LogWarning("[LLM] Provider override {Provider} is configured but no enabled provider matches it", overrideName);
            }

            return providers
                .OrderBy(p => p.Priority)
                .ThenBy(p => p.EffectiveName, StringComparer.OrdinalIgnoreCase);
        }

        private async Task<List<string>> DownloadImagesAsync(IList<string>? imageUrls, bool anyProviderSupportsVision)
        {
            var base64Images = new List<string>();
            if (imageUrls is not { Count: > 0 } || !anyProviderSupportsVision)
                return base64Images;

            foreach (var url in imageUrls)
            {
                try
                {
                    var bytes = await _httpClient.GetByteArrayAsync(url);
                    base64Images.Add(Convert.ToBase64String(bytes));
                    _logger.LogInformation("[LLM] Downloaded image {Url} ({Bytes} bytes)", url, bytes.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LLM] Failed to download image {Url}", url);
                }
            }

            return base64Images;
        }

        private string BuildSystemPrompt(IList<(string Author, string Content, bool IsBot)> contextMessages, bool requireResponse)
        {
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

            return systemContent.ToString();
        }

        private async Task<LlmProviderResponse> SendOllamaAsync(
            LlmProviderOptions provider,
            string systemContent,
            string userMessage,
            IReadOnlyList<string> base64Images)
        {
            var userMsg = new JsonObject
            {
                ["role"] = "user",
                ["content"] = userMessage
            };

            if (base64Images.Count > 0)
            {
                var imagesNode = new JsonArray();
                foreach (var b64 in base64Images)
                    imagesNode.Add(b64);
                userMsg["images"] = imagesNode;
            }

            var requestBody = new JsonObject
            {
                ["model"] = provider.Model,
                ["stream"] = false,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = systemContent },
                    userMsg
                }
            }.ToJsonString();

            using var response = await SendAsync(provider, requestBody);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == (HttpStatusCode)429)
                throw new LlmProviderRateLimitedException(provider.EffectiveName, json);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Ollama provider {provider.EffectiveName} failed with {(int)response.StatusCode}: {json}");

            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "I couldn't generate a response.";
            var promptTokens = TryGetInt64(doc.RootElement, "prompt_eval_count");
            var completionTokens = TryGetInt64(doc.RootElement, "eval_count");
            return new LlmProviderResponse(text, promptTokens, completionTokens);
        }

        private async Task<LlmProviderResponse> SendOpenAICompatibleAsync(
            LlmProviderOptions provider,
            string systemContent,
            string userMessage,
            IReadOnlyList<string> base64Images)
        {
            JsonNode userContent = JsonValue.Create(userMessage)!;
            if (base64Images.Count > 0)
            {
                var contentArray = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = userMessage }
                };

                foreach (var b64 in base64Images)
                {
                    contentArray.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = $"data:image/jpeg;base64,{b64}" }
                    });
                }

                userContent = contentArray;
            }

            var requestBody = new JsonObject
            {
                ["model"] = provider.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = systemContent },
                    new JsonObject { ["role"] = "user", ["content"] = userContent }
                }
            }.ToJsonString();

            using var response = await SendAsync(provider, requestBody);
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == (HttpStatusCode)429)
                throw new LlmProviderRateLimitedException(provider.EffectiveName, json);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"OpenAI-compatible provider {provider.EffectiveName} failed with {(int)response.StatusCode}: {json}");

            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "I couldn't generate a response.";

            long promptTokens = 0;
            long completionTokens = 0;
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                promptTokens = TryGetInt64(usage, "prompt_tokens");
                completionTokens = TryGetInt64(usage, "completion_tokens");
            }

            return new LlmProviderResponse(text, promptTokens, completionTokens);
        }

        private async Task<HttpResponseMessage> SendAsync(LlmProviderOptions provider, string requestBody)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl);
            var apiKey = provider.ResolveApiKey();
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            foreach (var (key, value) in provider.Headers)
                request.Headers.TryAddWithoutValidation(key, value);

            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));
            return await _httpClient.SendAsync(request, timeoutCts.Token);
        }

        private static long TryGetInt64(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var result)
                ? result
                : 0;
        }

        private sealed record LlmProviderResponse(string Text, long PromptTokens, long CompletionTokens);

        private sealed class LlmProviderRateLimitedException(string providerName, string responseBody)
            : Exception($"Provider {providerName} was rate-limited: {responseBody}");
    }
}
