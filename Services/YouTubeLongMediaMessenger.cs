using ChizuChan.Services.Interfaces;
using NetCord.Rest;

namespace ChizuChan.Services;

public sealed class YouTubeLongMediaMessenger(RestClient restClient) : IYouTubeLongMediaMessenger
{
    public async Task<ulong> SendAsync(YouTubeLongMediaJob job, CancellationToken cancellationToken)
    {
        // Resolve the owner's private channel using normal bot authentication, not any interaction/source token.
        var channel = await restClient.GetDMChannelAsync(job.OwnerUserId, cancellationToken: cancellationToken);
        var message = await restClient.SendMessageAsync(channel.Id, BuildMessage(job), cancellationToken: cancellationToken);
        return message.Id;
    }

    public static MessageProperties BuildMessage(YouTubeLongMediaJob job) => new()
    {
        Content = $"YouTube job `{job.Id}`: {job.State}.\n{YouTubeLongMediaStore.Bound(job.Message)}",
        AllowedMentions = AllowedMentionsProperties.None,
        Nonce = new NonceProperties(job.Id) { Unique = true },
    };
}
