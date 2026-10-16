using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.SharedKernel.Jobs;

public static class JobsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IJobEnqueuer"/> so a host or module can enqueue jobs. <paramref name="dataSource"/> opens connections as the
    /// app role. Needs <see cref="SharedKernelServiceCollectionExtensions.AddSharedKernel"/>.
    /// </summary>
    public static IServiceCollection AddJobEnqueuer(this IServiceCollection services, Func<IServiceProvider, DbDataSource> dataSource, Action<JobsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);
        var options = services.AddOptions<JobsOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton<DbDataSource>(dataSource);
        services.TryAddSingleton<IJobEnqueuer, JobEnqueuer>();
        return services;
    }

    /// <summary>
    /// Hosts the job claim loops, the recurring-job scheduler and the outbox relay (worker host only). Handlers, schedules and
    /// consumers come from <see cref="AddJobHandler{THandler}"/>, <see cref="AddRecurringJob"/> and <see cref="AddOutboxConsumer{TConsumer}"/>,
    /// which modules call from their own registration.
    /// </summary>
    public static IServiceCollection AddJobsWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<JobProcessor>();
        services.TryAddSingleton<RecurringJobScheduler>();
        services.TryAddSingleton<OutboxRelay>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobWorkerService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RecurringJobService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OutboxRelayService>());
        services.TryAddSingleton<JobsMetricsSampler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobsMetricsSampler>(provider => provider.GetRequiredService<JobsMetricsSampler>()));
        return services;
    }

    public static IServiceCollection AddJobHandler<THandler>(this IServiceCollection services)
        where THandler : class, IJobHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, THandler>());
        return services;
    }

    public static IServiceCollection AddOutboxConsumer<TConsumer>(this IServiceCollection services)
        where TConsumer : class, IOutboxConsumer
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOutboxConsumer, TConsumer>());
        return services;
    }

    /// <summary>Declares a job that the worker keeps enqueueing once per <paramref name="interval"/>. Pair it with an <see cref="IJobHandler"/> of the same kind.</summary>
    public static IServiceCollection AddRecurringJob(this IServiceCollection services, string kind, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        services.AddSingleton(new RecurringJobSchedule(kind, interval));
        return services;
    }

    /// <summary>Registers the <c>jobs</c> schema migrator for the Migrator host.</summary>
    public static IServiceCollection AddJobsMigrations(this IServiceCollection services, Func<IServiceProvider, DbDataSource> dataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);
        services.AddSingleton<IModuleMigrator>(provider => new JobsSchemaMigrator(dataSource(provider)));
        return services;
    }
}
