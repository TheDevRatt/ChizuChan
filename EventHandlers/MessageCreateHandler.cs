using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using System.Text.RegularExpressions;

namespace ChizuChan.EventHandlers
{
    public class MessageCreateHandler(
        ILogger<MessageCreateHandler> logger,
        GatewayClient gatewayClient,
        RestClient restClient,
        IOllamaService ollamaService,
        IOllamaModelState modelState,
        IMessageCacheService messageCache) : IMessageCreateGatewayHandler
    {
        private static readonly Regex ChizuPattern =
            new(@"\bchizu(?:-chan)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MentionPattern =
            new(@"<@!?(\d+)>", RegexOptions.Compiled);

        public async ValueTask HandleAsync(Message message)
        {
            if (message.Author.IsBot) return;

            ulong botId = gatewayClient.Id;

            // Always cache the message first so context builds up for every channel,
            // even when the AI is not triggered.
            messageCache.Add(
                message.ChannelId,
                message.Id,
                message.Author.Username,
                message.Content,
                isBot: false);

            bool isMentioned  = message.MentionedUsers.Any(u => u.Id == botId);
            bool isReplyToBot = message.ReferencedMessage is not null
                                && message.ReferencedMessage.Author.Id == botId;
            bool isNameOnly   = ChizuPattern.IsMatch(message.Content);

            if (!isMentioned && !isReplyToBot && !isNameOnly) return;

            bool requireResponse = isMentioned || isReplyToBot;

            logger.LogInformation(
                "Triggered by {Author} (mention={M} reply={R} name={N})",
                message.Author.Username, isMentioned, isReplyToBot, isNameOnly);

            try
            {
                await restClient.TriggerTypingAsync(message.ChannelId);

                // Resolve mentions: strip bot's own, replace others with @username
                var mentionLookup = message.MentionedUsers.ToDictionary(u => u.Id);
                var cleanContent = MentionPattern.Replace(message.Content, match =>
                {
                    if (!ulong.TryParse(match.Groups[1].Value, out var userId))
                        return match.Value;
                    if (userId == botId)
                        return "";
                    return mentionLookup.TryGetValue(userId, out var user)
                        ? $"@{user.Username}"
                        : match.Value;
                }).Trim();

                if (string.IsNullOrEmpty(cleanContent))
                    cleanContent = message.Content;

                // Fetch context from local cache — no REST call, no rate limiting
                var contextMessages = messageCache.GetRecent(
                    message.ChannelId, count: 10, excludeId: message.Id);

                logger.LogInformation("Context: {Count} cached messages", contextMessages.Count);

                // Collect image URLs when the current model supports vision
                IList<string>? imageUrls = null;
                if (modelState.IsVisionModel && message.Attachments.Count > 0)
                {
                    imageUrls = message.Attachments
                        .Where(a => a.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                        .Select(a => a.Url)
                        .ToList();

                    if (imageUrls.Count > 0)
                        logger.LogInformation("Found {Count} image attachment(s) to pass to vision model", imageUrls.Count);
                }

                var reply = await ollamaService.GenerateAsync(cleanContent, contextMessages, requireResponse, imageUrls);

                if (reply is null)
                {
                    logger.LogInformation("Model chose not to respond (indirect name mention)");
                    return;
                }

                // Cache the bot's own reply so it appears in future context
                messageCache.Add(message.ChannelId, 0, "Chizu", reply, isBot: true);

                await restClient.SendMessageAsync(
                    message.ChannelId,
                    new MessageProperties
                    {
                        Content          = reply,
                        MessageReference = MessageReferenceProperties.Reply(message.Id, false)
                    });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while generating AI response");
            }
        }
    }
}
