using ChizuChan.Services.Interfaces;

namespace ChizuChan.Services;

public sealed class YouTubeMusicActionHandler : IYouTubeMusicActionHandler
{
    public Task<YouTubeMusicActionResult> HandleAsync(
        ulong userId,
        string canonicalVideoId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(YouTubeMusicActionResult.Failed(
            "YouTube downloads are not configured yet."));
    }
}
