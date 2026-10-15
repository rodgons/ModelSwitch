using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ModelSwitch.SharedKernel;

public static class SharedKernelServiceCollectionExtensions
{
    /// <summary>Registers <see cref="TimeProvider.System"/> and the UUIDv7 <see cref="IGuidGenerator"/>.</summary>
    public static IServiceCollection AddSharedKernel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IGuidGenerator, GuidGenerator>();
        return services;
    }
}
