using NetCord;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;

namespace ChizuChan.Commands.Controllers;

public sealed class MusicSearchControllerModule : ComponentInteractionModule<MessageComponentInteractionContext>
{
    private readonly IMusicSearchNavigationCoordinator _navigationCoordinator;
    private readonly IMusicSearchActionCoordinator _actionCoordinator;
    private readonly RestClient _restClient;

    public MusicSearchControllerModule(
        IMusicSearchNavigationCoordinator navigationCoordinator,
        IMusicSearchActionCoordinator actionCoordinator,
        RestClient restClient)
    {
        _navigationCoordinator = navigationCoordinator;
        _actionCoordinator = actionCoordinator;
        _restClient = restClient;
    }

    [ComponentInteraction("music_search_previous")]
    public Task PreviousAsync() => NavigateAsync(moveNext: false);

    [ComponentInteraction("music_search_next")]
    public Task NextAsync() => NavigateAsync(moveNext: true);

    [ComponentInteraction("music_search_action")]
    public async Task ActionAsync()
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

        await _actionCoordinator.ExecuteAndCommitAsync(
            Context.User.Id,
            Context.Channel.Id,
            Context.Interaction.Message.Id,
            async (result, _) =>
            {
                await ModifyResponseAsync(message =>
                {
                    message.Content = result.Message;
                    message.Embeds = [];
                    message.Components = [];
                });
            });
    }

    private async Task NavigateAsync(bool moveNext)
    {
        var sourceMessageId = Context.Interaction.Message.Id;
        await _navigationCoordinator.NavigateAsync(
            Context.User.Id,
            Context.Channel.Id,
            sourceMessageId,
            moveNext,
            _ => RespondAsync(InteractionCallback.DeferredModifyMessage),
            async (update, _) =>
            {
                await _restClient.ModifyMessageAsync(
                    Context.Channel.Id,
                    sourceMessageId,
                    message =>
                    {
                        message.Content = update.Content;
                        message.Embeds = update.Embed is null ? [] : [update.Embed];
                        message.Components = update.Components;
                    });
            });
    }
}
