using ChizuChan.Options;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using System.Text;

namespace ChizuChan.Commands
{
    /// <summary>Choices shown in the /model slash command.</summary>
    public enum AiModel
    {
        [SlashCommandChoice(Name = "Qwen 2.5 3B (recommended, 4GB GPU)")]
        Qwen25_3b,

        [SlashCommandChoice(Name = "Phi 3.5 Mini 3.8B")]
        Phi35Mini,

        [SlashCommandChoice(Name = "Gemma 3 1B (fastest)")]
        Gemma3_1b,

        [SlashCommandChoice(Name = "Gemma 3 4B (Vision, heavier)")]
        Gemma3_4b,
    }

    public enum LlmProviderMode
    {
        [SlashCommandChoice(Name = "Auto Routing")]
        Auto,

        [SlashCommandChoice(Name = "Quality: Groq Llama 3.3 70B")]
        Quality,

        [SlashCommandChoice(Name = "Fast: Groq Llama 3.1 8B")]
        Fast,

        [SlashCommandChoice(Name = "OpenRouter Free")]
        OpenRouterFree,

        [SlashCommandChoice(Name = "Local Ollama")]
        LocalOllama,
    }

    public class AiCommandModule : ApplicationCommandModule<ApplicationCommandContext>
    {
        private static readonly Dictionary<AiModel, string> ModelMap = new()
        {
            [AiModel.Qwen25_3b]  = "qwen2.5:3b",
            [AiModel.Phi35Mini]  = "phi3.5",
            [AiModel.Gemma3_1b]  = "gemma3:1b",
            [AiModel.Gemma3_4b]  = "gemma3:4b",
        };

        private static readonly Dictionary<LlmProviderMode, string?> ProviderOverrideMap = new()
        {
            [LlmProviderMode.Auto] = null,
            [LlmProviderMode.Quality] = "groq-quality",
            [LlmProviderMode.Fast] = "groq-fast",
            [LlmProviderMode.OpenRouterFree] = "openrouter-free",
            [LlmProviderMode.LocalOllama] = "local-ollama",
        };

        private readonly IOllamaModelState _modelState;
        private readonly OllamaOptions _options;
        private readonly LlmUsageTracker _usageTracker;
        private readonly LlmProviderOverrideState _overrideState;

        public AiCommandModule(
            IOllamaModelState modelState,
            IOptions<OllamaOptions> options,
            LlmUsageTracker usageTracker,
            LlmProviderOverrideState overrideState)
        {
            _modelState = modelState;
            _options = options.Value;
            _usageTracker = usageTracker;
            _overrideState = overrideState;
        }

        [SlashCommand("model", "Switch the AI model Chizu uses.", Contexts = [InteractionContextType.Guild])]
        public async Task SetModelAsync(
            [SlashCommandParameter(Description = "Model to switch to")]
            AiModel model)
        {
            var modelName = ModelMap[model];
            _modelState.SetModel(modelName);

            var visionNote = _modelState.IsVisionModel ? " *(vision-capable — images will be read)*" : "";
            await RespondAsync(InteractionCallback.Message(
                new InteractionMessageProperties
                {
                    Content = $"Model switched to **{modelName}**{visionNote}",
                    Flags   = MessageFlags.Ephemeral
                }));
        }

        [SlashCommand("llm_provider", "Override Chizu's LLM provider, or return to automatic routing.", Contexts = [InteractionContextType.Guild])]
        public async Task SetProviderAsync(
            [SlashCommandParameter(Description = "Provider mode to use")]
            LlmProviderMode mode)
        {
            var providerName = ProviderOverrideMap[mode];
            if (providerName is null)
            {
                _overrideState.ClearOverride();
                await RespondAsync(InteractionCallback.Message(
                    new InteractionMessageProperties
                    {
                        Content = "LLM provider routing set to **auto**.",
                        Flags = MessageFlags.Ephemeral
                    }));
                return;
            }

            var exists = GetConfiguredProviders().Any(p =>
                string.Equals(p.EffectiveName, providerName, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                await RespondAsync(InteractionCallback.Message(
                    new InteractionMessageProperties
                    {
                        Content = $"Provider **{providerName}** is not configured in appsettings.json.",
                        Flags = MessageFlags.Ephemeral
                    }));
                return;
            }

            _overrideState.SetOverride(providerName);
            await RespondAsync(InteractionCallback.Message(
                new InteractionMessageProperties
                {
                    Content = $"LLM provider override set to **{providerName}**. If it fails or is rate-limited, Chizu will still fall back automatically.",
                    Flags = MessageFlags.Ephemeral
                }));
        }

        [SlashCommand("model_current", "Show which AI model Chizu is currently using.", Contexts = [InteractionContextType.Guild])]
        public async Task CurrentModelAsync()
        {
            var visionNote = _modelState.IsVisionModel ? " *(vision-capable)*" : "";
            await RespondAsync(InteractionCallback.Message(
                new InteractionMessageProperties
                {
                    Content = $"Currently using local model preference: **{_modelState.CurrentModel}**{visionNote}",
                    Flags   = MessageFlags.Ephemeral
                }));
        }

        [SlashCommand("llm_status", "Show Chizu's configured LLM providers and tracked usage.", Contexts = [InteractionContextType.Guild])]
        public async Task LlmStatusAsync()
        {
            var providers = GetConfiguredProviders().ToList();

            var sb = new StringBuilder("**LLM providers**\n");
            sb.Append("Override: **")
              .Append(string.IsNullOrWhiteSpace(_overrideState.OverrideProviderName) ? "auto" : _overrideState.OverrideProviderName)
              .Append("**\n");
            foreach (var provider in providers)
            {
                var snapshot = _usageTracker.GetSnapshot(provider.EffectiveName);
                var availability = _usageTracker.CanUse(provider) ? "available" : "exhausted/cooling down";
                var keyNote = provider.Kind == LlmProviderKind.OpenAICompatible
                    ? (string.IsNullOrWhiteSpace(provider.ResolveApiKey()) ? ", no key" : ", key ok")
                    : "";

                sb.Append("- ")
                  .Append(provider.Enabled ? "✅" : "⛔")
                  .Append(' ')
                  .Append(provider.EffectiveName)
                  .Append(" (`")
                  .Append(provider.Model)
                  .Append("`, ")
                  .Append(provider.Kind)
                  .Append(keyNote)
                  .Append("): ")
                  .Append(availability)
                  .Append(", requests today ")
                  .Append(snapshot.RequestsToday);

                if (provider.DailyRequestLimit > 0)
                    sb.Append('/').Append(provider.DailyRequestLimit);

                sb.Append(", tokens today ").Append(snapshot.TokensToday);

                if (snapshot.CooldownUntilUtc is { } cooldown && cooldown > DateTimeOffset.UtcNow)
                    sb.Append(", cooldown until ").Append(cooldown.ToLocalTime().ToString("g"));

                sb.Append('\n');
            }

            await RespondAsync(InteractionCallback.Message(
                new InteractionMessageProperties
                {
                    Content = sb.ToString().Length > 1900 ? sb.ToString()[..1900] + "…" : sb.ToString(),
                    Flags = MessageFlags.Ephemeral
                }));
        }
        private IEnumerable<LlmProviderOptions> GetConfiguredProviders()
        {
            return _options.Providers.Count > 0
                ? _options.Providers.OrderBy(p => p.Priority).ThenBy(p => p.EffectiveName)
                :
                [
                    new LlmProviderOptions
                    {
                        Name = "local-ollama",
                        Kind = LlmProviderKind.Ollama,
                        BaseUrl = _options.BaseUrl,
                        Model = _modelState.CurrentModel,
                        Enabled = true,
                        Priority = 100
                    }
                ];
        }
    }
}
