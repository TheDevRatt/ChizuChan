using NetCord.Gateway;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Extensions
{

    public enum PresenceKind { Playing, Streaming, Listening, Watching, Competing }

    public sealed record DynamicStatus(string Text, PresenceKind Kind, string? StreamingUrl = null);

    public interface IStatusProvider
    {
        /// Return null if you have nothing interesting right now.
        Task<DynamicStatus?> GetAsync(CancellationToken ct = default);
    }

    public static class PresenceMapping
    {
        public static UserActivityType ToUserActivityType(this PresenceKind k) => k switch
        {
            PresenceKind.Playing => UserActivityType.Playing,
            PresenceKind.Streaming => UserActivityType.Streaming,
            PresenceKind.Listening => UserActivityType.Listening,
            PresenceKind.Watching => UserActivityType.Watching,
            PresenceKind.Competing => UserActivityType.Competing,
            _ => UserActivityType.Playing
        };
    }

}
