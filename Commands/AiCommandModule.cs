using ChizuChan.Services.Interfaces;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace ChizuChan.Commands
{
    /// <summary>Choices shown in the /model slash command.</summary>
    public enum AiModel
    {
        [SlashCommandChoice(Name = "Gemma 3 12B (Vision)")]
        Gemma3_12b,

        [SlashCommandChoice(Name = "Nous Hermes 2")]
        NousHermes2,

        [SlashCommandChoice(Name = "Llama 2 Uncensored 7B")]
        Llama2Uncensored,
    }

    public class AiCommandModule : ApplicationCommandModule<ApplicationCommandContext>
    {
        private static readonly Dictionary<AiModel, string> ModelMap = new()
        {
            [AiModel.Gemma3_12b]       = "gemma3:4b",
            [AiModel.NousHermes2]      = "nous-hermes2:latest",
            [AiModel.Llama2Uncensored] = "llama2-uncensored:7b",
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
