using ChizuChan.Extensions;
using ChizuChan.Options;
using ChizuChan.Providers;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;

namespace ChizuChan
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Program.cs
            var builder = Host.CreateApplicationBuilder(args);

            // Make sure logs show up
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(o =>
            {
                o.TimestampFormat = "HH:mm:ss ";
                o.SingleLine = true;
            });
            builder.Logging.SetMinimumLevel(LogLevel.Information);

            builder.Services.AddDiscordGateway(options =>
            {
                options.Intents = NetCord.Gateway.GatewayIntents.All;

                // (Smoke test) initial presence so you immediately see a status
                options.Presence = new NetCord.Gateway.PresenceProperties(NetCord.UserStatusType.Online)
                {
                    Activities = [new UserActivityProperties("/play", UserActivityType.Listening)]
                };
            })
            .AddGatewayEventHandlers(typeof(Program).Assembly)
            .AddApplicationCommands()
            .AddComponentInteractions()
            .Configure<ApiKeyOptions>(builder.Configuration.GetSection("ApiKeys"))
            .AddAllServicesFromAssembly(typeof(Program).Assembly)
            .AddHttpClient();

            // Explicit registrations (keep these even if you scan)
            builder.Services.AddSingleton<IStatusProvider, WeatherStatusProvider>();
            builder.Services.AddHostedService<StatusRotatorService>();

            var host = builder.Build()
                .UseGatewayEventHandlers()
                .AddModules(typeof(Program).Assembly);

            await host.RunAsync();
        }
    }
}
