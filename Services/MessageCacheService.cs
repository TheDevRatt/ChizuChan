using ChizuChan.Services.Interfaces;
using System.Collections.Concurrent;

namespace ChizuChan.Services
{
    public class MessageCacheService : IMessageCacheService
    {
        // How many messages to keep per channel
        private const int MaxPerChannel = 25;

        private record CachedMessage(ulong Id, string Author, string Content, bool IsBot);

        // Each channel gets its own linked list, guarded by a per-list lock
        private readonly ConcurrentDictionary<ulong, LinkedList<CachedMessage>> _cache = new();

        public void Add(ulong channelId, ulong messageId, string author, string content, bool isBot)
        {
            var list = _cache.GetOrAdd(channelId, _ => new LinkedList<CachedMessage>());
            lock (list)
            {
                list.AddLast(new CachedMessage(messageId, author, content, isBot));
                while (list.Count > MaxPerChannel)
                    list.RemoveFirst();
            }
        }

        public IList<(string Author, string Content, bool IsBot)> GetRecent(
            ulong channelId, int count, ulong excludeId = 0)
        {
            if (!_cache.TryGetValue(channelId, out var list))
                return [];

            lock (list)
            {
                return list
                    .Where(m => m.Id != excludeId && !string.IsNullOrWhiteSpace(m.Content))
                    .TakeLast(count)
                    .Select(m => (m.Author, m.Content, m.IsBot))
                    .ToList();
            }
        }
    }
}
