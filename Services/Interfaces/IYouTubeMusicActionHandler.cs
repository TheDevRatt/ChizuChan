namespace ChizuChan.Services.Interfaces;

public readonly record struct YouTubeMusicActionResult(bool Success, string Message)
{
    public static YouTubeMusicActionResult Succeeded(string message) => new(true, message);
    public static YouTubeMusicActionResult Failed(string message) => new(false, message);
}

public interface IYouTubeMusicActionHandler
{
    Task<YouTubeMusicActionResult> HandleAsync(
        ulong userId,
        string canonicalVideoId,
        CancellationToken cancellationToken = default);
}
