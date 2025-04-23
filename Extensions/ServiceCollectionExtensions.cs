using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Extensions
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Automatically registers all services in the specified assembly
        /// where classes implement an interface that matches the convention IClassName → ClassName.
        /// Each matching pair is registered as a singleton.
        /// </summary>
        /// <param name="services">The IServiceCollection to which services will be added.</param>
        /// <param name="assembly">The assembly to scan for service implementations.</param>
        /// <returns>The same IServiceCollection with added registrations.</returns>
        public static IServiceCollection AddAllServicesFromAssembly(this IServiceCollection services, Assembly assembly)
        {
            Type[] allTypes = assembly.GetTypes();

            foreach (Type candidateType in allTypes)
            {
                if (candidateType.IsClass && !candidateType.IsAbstract)
                {
                    Type[] interfaceTypes = candidateType.GetInterfaces();

                    foreach (Type interfaceType in interfaceTypes)
                    {
                        string expectedInterfaceName = "I" + candidateType.Name;

                        if (interfaceType.Name == expectedInterfaceName)
                        {
                            services.AddSingleton(interfaceType, candidateType);
                        }
                    }
                }
            }

            return services;
        }
    }
}
