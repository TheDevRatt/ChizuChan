using ChizuChan;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

namespace ChizuChan.LongMedia.Delivery.Tests;

public class DeliveryRegistrationTests
{
    [Fact]
    public void Production_exposes_shared_durable_delivery_registration()
    {
        Assert.NotNull(typeof(Program).GetMethod("AddYouTubeLongMediaDelivery"));
    }

    [Fact]
    public void Production_context_assembly_scan_discovers_direct_status_and_search_routes()
    {
        var commands = new ApplicationCommandService<ApplicationCommandContext>();
        ((IService)commands).AddModules(typeof(Program).Assembly);
        Assert.Contains(commands.GetCommands(), command => command.Name == "youtube_download");
        Assert.Contains(commands.GetCommands(), command => command.Name == "youtube_job");
        var components = new ComponentInteractionService<ComponentInteractionContext>();
        ((IService)components).AddModules(typeof(Program).Assembly);
        Assert.Contains(components.GetComponentInteractions(), command => command.Key.ToString() == "music_search_action");
    }
}
