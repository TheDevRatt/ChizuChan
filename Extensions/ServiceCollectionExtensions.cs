using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ChizuChan.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddAllServicesFromAssembly(this IServiceCollection services, Assembly assembly)
        {
            foreach (var impl in assembly.GetTypes())
            {
                // Only public, non-abstract, non-generic classes, not compiler-generated
                if (!impl.IsClass || impl.IsAbstract || impl.IsGenericTypeDefinition || !IsPublic(impl))
                    continue;

                if (impl.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
                    continue;

                var matchingInterfaces = impl.GetInterfaces()
                    .Where(i =>
                        IsPublic(i) &&
                        i.Assembly == assembly &&                    // exclude framework interfaces (e.g., IEnumerator)
                        i.Name == $"I{impl.Name}")                   // convention: IClassName
                    .ToArray();

                foreach (var @interface in matchingInterfaces)
                {
                    services.AddSingleton(@interface, impl);
                }
            }

            return services;
        }

        private static bool IsPublic(Type t) => t.IsPublic || t.IsNestedPublic;
    }
}
