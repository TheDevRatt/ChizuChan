namespace ChizuChan.Services.Interfaces;

public interface IMusicSearchInteractionGate
{
    ValueTask<IAsyncDisposable> EnterAsync(
        ulong userId,
        ulong dmChannelId,
        ulong sourceMessageId,
        CancellationToken cancellationToken = default);
}
