using NetCord;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;

namespace ChizuChan.Commands.Controllers;

public sealed class MusicSearchControllerModule : ComponentInteractionModule<ComponentInteractionContext>
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
    public Task ActionAsync(string actionTokenSegment, int index)
    {
        var sourceMessageId = ((MessageComponentInteraction)Context.Interaction).Message.Id;
        return _actionCoordinator.ExecuteAndCommitAsync(
            Context.User.Id,
            Context.Channel.Id,
            sourceMessageId,
            actionTokenSegment,
            index,
            _ => RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral)),
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
        var sourceMessageId = ((MessageComponentInteraction)Context.Interaction).Message.Id;
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
