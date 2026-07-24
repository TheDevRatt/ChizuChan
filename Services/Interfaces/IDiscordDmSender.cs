using ChizuChan.DTOs;

namespace ChizuChan.Services.Interfaces;

public interface IDiscordDmSender
{
    Task<ulong> SendCompletionAsync(
        MusicRequestNotificationDTO notification,
        CancellationToken cancellationToken = default);
}
