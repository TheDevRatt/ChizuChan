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

    public class AiCommandModule : ApplicationCommandModule<ApplicationCommandContext>
    {
        private static readonly Dictionary<AiModel, string> ModelMap = new()
        {
            [AiModel.Qwen25_3b]  = "qwen2.5:3b",
            [AiModel.Phi35Mini]  = "phi3.5",
            [AiModel.Gemma3_1b]  = "gemma3:1b",
            [AiModel.Gemma3_4b]  = "gemma3:4b",
        };

        private readonly IOllamaModelState _modelState;
        private readonly OllamaOptions _options;
        private readonly LlmUsageTracker _usageTracker;

        public AiCommandModule(
            IOllamaModelState modelState,
            IOptions<OllamaOptions> options,
            LlmUsageTracker usageTracker)
        {
            _modelState = modelState;
            _options = options.Value;
            _usageTracker = usageTracker;
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
            var providers = _options.Providers.Count > 0
                ? _options.Providers.OrderBy(p => p.Priority).ThenBy(p => p.EffectiveName).ToList()
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

            var sb = new StringBuilder("**LLM providers**\n");
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
    }
}
