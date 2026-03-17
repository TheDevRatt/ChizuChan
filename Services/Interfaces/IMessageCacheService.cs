namespace ChizuChan.Services.Interfaces
{
    public interface IMessageCacheService
    {
        /// <summary>Adds a message to the channel's rolling cache.</summary>
        void Add(ulong channelId, ulong messageId, string author, string content, bool isBot);

        /// <summary>
        /// Returns up to <paramref name="count"/> recent messages for a channel,
        /// oldest first, optionally excluding a specific message ID.
        /// </summary>
        IList<(string Author, string Content, bool IsBot)> GetRecent(
            ulong channelId, int count, ulong excludeId = 0);
    }
}
