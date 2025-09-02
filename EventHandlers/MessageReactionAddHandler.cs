using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.EventHandlers
{
    [GatewayEvent(nameof(GatewayClient.MessageReactionAdd))]
    public class MessageReactionAddHandler(RestClient client) : IGatewayEventHandler<MessageReactionAddEventArgs>
    {
        public async ValueTask HandleAsync(MessageReactionAddEventArgs args)
        {
            //await client.SendMessageAsync(args.ChannelId, $"<@{args.UserId}> reacted with {args.Emoji.Name}!");
            Console.WriteLine($"<@{args.UserId}> reacted with {args.Emoji.Name} in channel {args.ChannelId}!");
        }
    }
}
