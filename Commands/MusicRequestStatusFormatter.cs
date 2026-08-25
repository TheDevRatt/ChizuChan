using System.Text;
using ChizuChan.DTOs;

namespace ChizuChan.Commands;

public static class MusicRequestStatusFormatter
{
    public static string Build(IReadOnlyList<DirectMusicRequestStatusDTO> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            return "You don't have any tracked direct Soulseek requests yet.";

        var builder = new StringBuilder("**Your direct Soulseek requests**");
        foreach (var request in requests.Take(10))
        {
            var artist = Escape(request.ArtistName, 70);
            var title = Escape(request.TrackTitle, 70);
            builder.Append("\n• **").Append(artist).Append(" — ").Append(title).Append("**\n  ")
                .Append(Label(request));
        }
        return builder.Length <= 2000 ? builder.ToString() : builder.ToString(0, 1999) + "…";
    }

    private static string Label(DirectMusicRequestStatusDTO request) => request.State switch
    {
        DirectMusicRequestState.DispatchPending or DirectMusicRequestState.Queued => "Queued",
        DirectMusicRequestState.Downloading => $"Downloading • {Percentage(request)}%",
        DirectMusicRequestState.DownloadedWaitingForPlex => "Downloaded • waiting for Plex",
        DirectMusicRequestState.ReadyInPlexamp => "Ready in Plexamp",
        DirectMusicRequestState.Failed => $"Failed • {FailureReason(request.FailureCategory)}",
        _ => "Status unavailable",
    };

    private static int Percentage(DirectMusicRequestStatusDTO request)
    {
        if (request.ExpectedSize <= 0 || request.BytesTransferred <= 0)
            return 0;
        return (int)Math.Clamp(
            request.BytesTransferred * 100L / request.ExpectedSize,
            0,
            100);
    }

    private static string FailureReason(string? category) => category switch
    {
        "QueueRejected" => "slskd rejected the queue request",
        "TransferCancelled" => "the Soulseek transfer was cancelled",
        "TransferTimedOut" => "the Soulseek transfer timed out",
        "TransferMissing" => "slskd no longer has the transfer",
        "TransferMismatch" => "the transfer no longer matches this request",
        "PlexIndexTimeout" => "Plex didn't index it in time",
        _ => "the Soulseek transfer failed",
    };

    private static string Escape(string? value, int maximumLength)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
        if (source.Length > maximumLength)
            source = source[..(maximumLength - 1)] + "…";

        var builder = new StringBuilder(source.Length + 8);
        foreach (var character in source)
        {
            if (character == '@')
            {
                builder.Append("@\u200B");
                continue;
            }
            if (character is '\\' or '*' or '_' or '~' or '`' or '>' or '|' or '[' or ']' or '(' or ')')
                builder.Append('\\');
            if (!char.IsControl(character))
                builder.Append(character);
        }
        return builder.ToString();
    }
}
