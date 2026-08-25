namespace ChizuChan.Services.Interfaces;

public interface IDirectMusicRequestStatusProcessor
{
    Task ProcessOnceAsync(CancellationToken cancellationToken = default);
}
