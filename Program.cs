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
using Microsoft.Extensions.Options;

namespace ChizuChan
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Program.cs
            var builder = Host.CreateApplicationBuilder(args);

            AddMachineLocalSecrets(builder.Configuration, builder.Environment, args);

            // Make sure logs show up
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(o =>
            {
                o.TimestampFormat = "HH:mm:ss ";
                o.SingleLine = true;
            });
            builder.Logging.SetMinimumLevel(LogLevel.Information);

            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

            builder.Services.AddDiscordGateway(options =>
            {
                options.Intents = NetCord.Gateway.GatewayIntents.All;

                // (Smoke test) initial presence so you immediately see a status
                options.Presence = new NetCord.Gateway.PresenceProperties(NetCord.UserStatusType.Online)
                {
                    Activities = [new UserActivityProperties("/play", UserActivityType.Listening)]
                };
            })
            .AddGatewayHandlers(typeof(Program).Assembly)
            .AddApplicationCommands()
            .AddComponentInteractions()
            .Configure<ApiKeyOptions>(builder.Configuration.GetSection("ApiKeys"))
            .Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"))
            .Configure<LidarrOptions>(builder.Configuration.GetSection(LidarrOptions.SectionName))
            .AddAllServicesFromAssembly(typeof(Program).Assembly)
            .AddHttpClient();

            // Explicit registrations (keep these even if you scan)
            builder.Services.AddSingleton<IStatusProvider, WeatherStatusProvider>();
            builder.Services.AddSingleton<LlmUsageTracker>();
            builder.Services.AddSingleton<LlmProviderOverrideState>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
                var logger = sp.GetRequiredService<ILogger<LlmProviderOverrideState>>();
                var state = new LlmProviderOverrideState();
                state.UseStore(options.OverrideStorePath, logger);
                return state;
            });
            builder.Services.AddHostedService<StatusRotatorService>();

            var host = builder.Build()
                .AddModules(typeof(Program).Assembly);

            await host.RunAsync();
        }

        private static void AddMachineLocalSecrets(
            IConfigurationBuilder configuration,
            IHostEnvironment environment,
            string[] args)
        {
            var configuredPath = Environment.GetEnvironmentVariable("CHIZUCHAN_SECRETS_PATH");
            var secretsPath = configuredPath;

            if (!string.IsNullOrWhiteSpace(configuredPath) && !Path.IsPathFullyQualified(configuredPath))
            {
                throw new ArgumentException(
                    "CHIZUCHAN_SECRETS_PATH must be an absolute path.",
                    nameof(configuredPath));
            }

            if (string.IsNullOrWhiteSpace(secretsPath) && OperatingSystem.IsWindows() && !environment.IsDevelopment())
            {
                secretsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ChizuChan",
                    "secrets.json");
            }

            if (string.IsNullOrWhiteSpace(secretsPath))
            {
                return;
            }

            if (!File.Exists(secretsPath))
            {
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    throw new FileNotFoundException(
                        "CHIZUCHAN_SECRETS_PATH points to a file that does not exist.",
                        secretsPath);
                }

                return;
            }

            configuration.AddJsonFile(secretsPath, optional: false, reloadOnChange: false);

            // Restore normal .NET precedence after inserting the machine-local file:
            // environment variables and command-line arguments remain the final overrides.
            configuration.AddEnvironmentVariables();
            if (args.Length > 0)
            {
                configuration.AddCommandLine(args);
            }
        }
    }
}
