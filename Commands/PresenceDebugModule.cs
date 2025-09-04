using ChizuChan.Extensions;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Commands
{
    public sealed class PresenceDebugModule : ApplicationCommandModule<ApplicationCommandContext>
    {
        private readonly GatewayClient _client;
        private readonly IEnumerable<IStatusProvider> _providers;

        public PresenceDebugModule(GatewayClient client, IEnumerable<IStatusProvider> providers)
        {
            _client = client; _providers = providers;
        }

        [SlashCommand("presence_test", "Fetch a status from providers and apply it", Contexts = [InteractionContextType.Guild])]
        public async Task PresenceTestAsync()
        {
            await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

            DynamicStatus? s = null;
            foreach (var p in _providers)
            {
                s = await p.GetAsync();
                if (s is not null) break;
            }

            if (s is null)
            {
                await ModifyResponseAsync(m => m.Content = "No provider returned a status.");
                return;
            }

            var presence = new PresenceProperties(UserStatusType.Online)
            {
                Activities = [ new UserActivityProperties(s.Text, s.Kind.ToUserActivityType())
            {
                Url = s.Kind == PresenceKind.Streaming ? s.StreamingUrl : null
            }]
            };

            await _client.UpdatePresenceAsync(presence);
            await ModifyResponseAsync(m => m.Content = $"Applied: {s.Text} [{s.Kind}]");
        }
    }
}
