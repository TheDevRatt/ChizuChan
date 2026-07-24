using ChizuChan.DTOs;
using ChizuChan.Services.Interfaces;
using NetCord.Rest;

namespace ChizuChan.Commands;

public sealed record MusicSearchNavigationUpdate(
    string? Content,
    EmbedProperties? Embed,
    IMessageComponentProperties[] Components,
    MusicSearchSessionSnapshot? Snapshot);

public interface IMusicSearchNavigationCoordinator
{
    Task NavigateAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        bool moveNext,
        Func<CancellationToken, Task> acknowledgeAsync,
        Func<MusicSearchNavigationUpdate, CancellationToken, Task> modifyMessageAsync,
        CancellationToken cancellationToken = default);
}

public sealed class MusicSearchNavigationCoordinator : IMusicSearchNavigationCoordinator
{
    private const string ExpiredMessage =
        "This music search has expired or was superseded. Run `/music_search` again.";

    private readonly IMusicSearchSessionService _sessionService;
    private readonly IMusicSearchEmbedBuilder _embedBuilder;
    private readonly IMusicSearchInteractionGate _interactionGate;

    public MusicSearchNavigationCoordinator(
        IMusicSearchSessionService sessionService,
        IMusicSearchEmbedBuilder embedBuilder,
        IMusicSearchInteractionGate interactionGate)
    {
        _sessionService = sessionService;
        _embedBuilder = embedBuilder;
        _interactionGate = interactionGate;
    }

    public async Task NavigateAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        bool moveNext,
        Func<CancellationToken, Task> acknowledgeAsync,
        Func<MusicSearchNavigationUpdate, CancellationToken, Task> modifyMessageAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgeAsync);
        ArgumentNullException.ThrowIfNull(modifyMessageAsync);

        await acknowledgeAsync(cancellationToken);

        await using var lease = await _interactionGate.EnterAsync(
            userId, dmChannelId, sourceMessageId, cancellationToken);

        var found = moveNext
            ? _sessionService.MoveNext(userId, dmChannelId, sourceMessageId, out var snapshot)
            : _sessionService.MovePrevious(userId, dmChannelId, sourceMessageId, out snapshot);

        if (!found)
        {
            await modifyMessageAsync(
                new MusicSearchNavigationUpdate(ExpiredMessage, null, [], null),
                cancellationToken);
            return;
        }

        var rendered = _embedBuilder.Build(snapshot.Query, snapshot);
        await modifyMessageAsync(
            new MusicSearchNavigationUpdate(null, rendered.Embed, rendered.Components, snapshot),
            cancellationToken);
    }
}
