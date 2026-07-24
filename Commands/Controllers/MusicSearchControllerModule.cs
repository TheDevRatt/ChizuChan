using ChizuChan.Services.Interfaces;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;

namespace ChizuChan.Commands.Controllers;

public sealed class MusicSearchControllerModule : ComponentInteractionModule<MessageComponentInteractionContext>
{
    private readonly IMusicSearchSessionService _sessionService;
    private readonly IMusicSearchEmbedBuilder _embedBuilder;
    private readonly IMusicSearchActionCoordinator _actionCoordinator;
    private readonly RestClient _restClient;

    public MusicSearchControllerModule(
        IMusicSearchSessionService sessionService,
        IMusicSearchEmbedBuilder embedBuilder,
        IMusicSearchActionCoordinator actionCoordinator,
        RestClient restClient)
    {
        _sessionService = sessionService;
        _embedBuilder = embedBuilder;
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

        var result = await _actionCoordinator.ExecuteAsync(
            Context.User.Id,
            Context.Channel.Id,
            Context.Interaction.Message.Id);
        await ModifyResponseAsync(message =>
        {
            message.Content = result.Message;
            message.Embeds = [];
            message.Components = [];
        });
    }

    private async Task NavigateAsync(bool moveNext)
    {
        await RespondAsync(InteractionCallback.DeferredModifyMessage);

        var sourceMessageId = Context.Interaction.Message.Id;
        var found = moveNext
            ? _sessionService.MoveNext(
                Context.User.Id, Context.Channel.Id, sourceMessageId, out var snapshot)
            : _sessionService.MovePrevious(
                Context.User.Id, Context.Channel.Id, sourceMessageId, out snapshot);

        if (!found)
        {
            await _restClient.ModifyMessageAsync(
                Context.Channel.Id,
                sourceMessageId,
                message =>
                {
                    message.Content = "This music search has expired or was superseded. Run `/music_search` again.";
                    message.Embeds = [];
                    message.Components = [];
                });
            return;
        }

        var rendered = _embedBuilder.Build(snapshot.Query, snapshot);
        await _restClient.ModifyMessageAsync(
            Context.Channel.Id,
            sourceMessageId,
            message =>
            {
                message.Content = null;
                message.Embeds = [rendered.Embed];
                message.Components = rendered.Components;
            });
    }
}
