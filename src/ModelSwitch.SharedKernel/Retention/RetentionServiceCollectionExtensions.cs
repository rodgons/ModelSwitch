using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelSwitch.SharedKernel.Jobs;

namespace ModelSwitch.SharedKernel.Retention;

public static class RetentionServiceCollectionExtensions
{
    /// <summary>
    /// The daily <c>retention.run</c> job for the worker host. Modules contribute their tables with <see cref="AddRetentionTask{TTask}"/>;
    /// <paramref name="source"/> opens connections as the retention role (or the app role, see <see cref="RetentionDataSource"/>).
    /// </summary>
    public static IServiceCollection AddRetention(this IServiceCollection services, Func<IServiceProvider, RetentionDataSource> source)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(source);
        services.TryAddSingleton(provider => RetentionOptions.From(provider.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton(source);
        services.AddJobHandler<RetentionJob>();
        services.AddRecurringJob(RetentionJob.JobKind, RetentionJob.Interval);
        services.AddRetentionTask<JobsRetentionTask>();
        return services;
    }

    /// <summary>Adds a module's retention task. It only runs in a host that called <see cref="AddRetention"/>.</summary>
    public static IServiceCollection AddRetentionTask<TTask>(this IServiceCollection services)
        where TTask : class, IRetentionTask
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTask, TTask>());
        return services;
    }
}
