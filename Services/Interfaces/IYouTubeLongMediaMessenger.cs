namespace ChizuChan.Services.Interfaces;

/// <summary>Authenticated bot delivery, never an interaction webhook/token.</summary>
public interface IYouTubeLongMediaMessenger
{
    Task<ulong> SendAsync(YouTubeLongMediaJob job, CancellationToken cancellationToken);
}
