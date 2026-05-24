using ChizuChan.Services.Interfaces;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

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

        public AiCommandModule(IOllamaModelState modelState)
        {
            _modelState = modelState;
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
                    Content = $"Currently using: **{_modelState.CurrentModel}**{visionNote}",
                    Flags   = MessageFlags.Ephemeral
                }));
        }
    }
}
