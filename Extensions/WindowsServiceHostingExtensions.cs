using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChizuChan.Extensions;

public static class WindowsServiceHostingExtensions
{
    public const string ServiceName = "ChizuChan";

    public static IServiceCollection AddChizuChanWindowsService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddWindowsService(ConfigureChizuChanService);
    }

    public static void ConfigureChizuChanService(WindowsServiceLifetimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ServiceName = ServiceName;
    }
}