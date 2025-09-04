using ChizuChan.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Services
{

    public sealed class StatusRotatorService : BackgroundService
    {
        private readonly GatewayClient _client;
        private readonly IEnumerable<IStatusProvider> _providers;
        private readonly ILogger<StatusRotatorService> _log;

        // keep the CTS so we can reuse it in the Ready handler
        private CancellationToken _stoppingToken;

        public StatusRotatorService(GatewayClient client,
                                    IEnumerable<IStatusProvider> providers,
                                    ILogger<StatusRotatorService> log)
        {
            _client = client;
            _providers = providers;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _stoppingToken = ct;

            _client.Ready += OnReady;

            _ = SafeUpdateAsync("initial", ct);

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await SafeUpdateAsync("timer", ct);

            ValueTask OnReady(ReadyEventArgs args)
            {
                _ = SafeUpdateAsync("ready", _stoppingToken);
                return default;
            }
        }

        private async Task SafeUpdateAsync(string reason, CancellationToken ct)
        {
            try
            {
                DynamicStatus? chosen = null;
                foreach (var p in _providers)
                {
                    try
                    {
                        var r = await p.GetAsync(ct).ConfigureAwait(false);
                        _log.LogInformation("Presence: {Provider} -> {Has}", p.GetType().Name, r is not null ? "value" : "null");
                        if (r is not null) { chosen = r; break; }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Presence: provider {Provider} failed", p.GetType().Name);
                    }
                }

                chosen ??= new DynamicStatus("Playing music | /play", PresenceKind.Playing);

                var presence = new PresenceProperties(UserStatusType.Online)
                {
                    Activities =
                    [
                        new UserActivityProperties(chosen.Text, chosen.Kind.ToUserActivityType())
                    {
                        Url = chosen.Kind == PresenceKind.Streaming ? chosen.StreamingUrl : null
                    }
                    ]
                };

                await _client.UpdatePresenceAsync(presence).ConfigureAwait(false);
                _log.LogInformation("Presence updated ({Reason}): {Text} [{Kind}]", reason, chosen.Text, chosen.Kind);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Presence update failed ({Reason})", reason);
            }
        }
    }
}
