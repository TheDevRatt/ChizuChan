﻿using ChizuChan.Extensions;
using ChizuChan.Options;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
            var builder = Host.CreateApplicationBuilder(args);

            builder.Services.AddDiscordGateway(options =>
            {
                options.Intents = NetCord.Gateway.GatewayIntents.All;
            })
            .AddGatewayEventHandlers(typeof(Program).Assembly)
            .AddApplicationCommands()
            .AddComponentInteractions()
            .Configure<ApiKeyOptions>(builder.Configuration.GetSection("ApiKeys"))
            .AddAllServicesFromAssembly(typeof(Program).Assembly)
            .AddHttpClient();

            var host = builder.Build()
                .UseGatewayEventHandlers()
                .AddModules(typeof(Program).Assembly);

            await host.RunAsync();
        }
    }
}
