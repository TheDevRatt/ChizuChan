namespace ChizuChan.Services.Interfaces;

public interface IMusicRequestCompletionProcessor
{
    Task ProcessOnceAsync(CancellationToken cancellationToken = default);
}
