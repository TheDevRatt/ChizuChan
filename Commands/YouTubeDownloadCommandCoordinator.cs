using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChizuChan.Commands;

public readonly record struct YouTubeDownloadCommandResult(bool Success, string Message);

public static class YouTubeDownloadCommandCoordinator
{
    private const int MaximumMessageLength = 500;

    public static async Task<YouTubeDownloadCommandResult> ExecuteAsync(
        ulong userId,
        string? url,
        IMusicRequestAccessService accessService,
        IYouTubeMusicActionHandler handler,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessService);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(logger);

        var access = accessService.CheckAccess(userId, MusicRequestOperation.Download);
        if (access.Status == MusicRequestAccessStatus.Unauthorized)
            return Failed("You don't have permission to use music requests.");
        if (access.Status == MusicRequestAccessStatus.RateLimited)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(access.RetryAfter.TotalSeconds));
            return Failed($"Please wait {seconds}s before trying again.");
        }

        if (!YouTubeDownloadUrlParser.TryParse(url, out var videoId))
            return Failed("Please provide a supported HTTPS YouTube video URL.");

        try
        {
            var result = await handler.HandleAsync(userId, videoId, cancellationToken);
            return new YouTubeDownloadCommandResult(result.Success, BoundMessage(result.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Direct YouTube download failed ({ExceptionType}).",
                exception.GetType().Name);
            return Failed("Couldn't download that YouTube track right now.");
        }
    }

    private static YouTubeDownloadCommandResult Failed(string message) => new(false, message);

    private static string BoundMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Couldn't download that YouTube track right now.";
        return message.Length <= MaximumMessageLength
            ? message
            : message[..(MaximumMessageLength - 1)] + "…";
    }
}
