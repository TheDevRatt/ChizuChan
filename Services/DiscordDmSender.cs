using System.Text;
using ChizuChan.DTOs;
using ChizuChan.Services.Interfaces;
using NetCord.Rest;

namespace ChizuChan.Services;

/// <summary>Sends completion notifications to the exact DM channel captured at request time.</summary>
public sealed class DiscordDmSender(RestClient restClient) : IDiscordDmSender
{
    private const int MaximumNameLength = 400;

    public async Task<ulong> SendCompletionAsync(
        MusicRequestNotificationDTO notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.DmChannelId == 0)
            throw new ArgumentException("A DM channel ID is required.", nameof(notification));

        var message = await restClient.SendMessageAsync(
            notification.DmChannelId,
            BuildMessageProperties(notification),
            cancellationToken: cancellationToken);
        if (message.Id == 0)
            throw new InvalidDataException("Discord returned an invalid message identity.");
        return message.Id;
    }

    public static MessageProperties BuildMessageProperties(MusicRequestNotificationDTO notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (string.IsNullOrWhiteSpace(notification.NotificationNonce))
            throw new ArgumentException("A persisted notification nonce is required.", nameof(notification));

        var artist = EscapeDiscordText(notification.ArtistName, MaximumNameLength);
        var album = EscapeDiscordText(notification.AlbumTitle, MaximumNameLength);
        var content = $"🎵 **Your music is ready!**\n{artist} — {album} is now available in Plex/Plexamp.";
        if (content.Length > 2000)
            content = content[..2000];

        return new MessageProperties
        {
            Content = content,
            Nonce = new NonceProperties(notification.NotificationNonce) { Unique = true },
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }

    private static string EscapeDiscordText(string? value, int maximumLength)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
        if (source.Length > maximumLength)
            source = source[..(maximumLength - 1)] + "…";

        var result = new StringBuilder(source.Length + 16);
        foreach (var character in source)
        {
            if (character == '@')
            {
                result.Append("@\u200B");
                continue;
            }

            if (character is '\\' or '*' or '_' or '~' or '`' or '>' or '|' or '[' or ']' or '(' or ')')
                result.Append('\\');
            result.Append(character);
        }

        return result.ToString();
    }
}
