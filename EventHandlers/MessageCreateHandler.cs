using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.EventHandlers
{
    [GatewayEvent(nameof(GatewayClient.MessageCreate))]
    public class MessageCreateHandler(ILogger<MessageCreateHandler> logger) : IGatewayEventHandler<Message>
    {
        public ValueTask HandleAsync(Message message)
        {
            logger.LogInformation("{}", message.Content);
            return default;
        }
    }
}
