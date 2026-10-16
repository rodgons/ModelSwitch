using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelSwitch.Inference.Application;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Inference.Endpoints;
using ModelSwitch.Inference.Infrastructure;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Http;

namespace ModelSwitch.Inference;

public static class InferenceModule
{
    public static IServiceCollection AddInferenceModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(provider => InferenceOptions.From(provider.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton<IJitterSource, RandomJitterSource>();
        services.TryAddSingleton<AttemptExecutor>();
        services.AddMetrics();
        services.TryAddSingleton<InferenceMetrics>();
        services.TryAddSingleton<LimitsMetrics>();
        services.TryAddSingleton<IInferenceService, InferenceService>();
        services.TryAddSingleton<ChatCompletionsService>();
        services.TryAddSingleton<ModelListService>();
        return services;
    }

    /// <summary>
    /// Registers step 6 for the api host: the <see cref="ILimiter"/> over Valkey (<c>ConnectionStrings:Valkey</c>, required) and the
    /// Valkey readiness check, which only applies when <c>Limits:FailureMode</c> is <c>Closed</c> (api.md section 4). Not for the worker.
    /// </summary>
    public static IServiceCollection AddInferenceLimits(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RequireSetting("ConnectionStrings:Valkey");
        services.TryAddSingleton(provider => LimitsOptions.From(provider.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton<LimitsMetrics>();
        services.TryAddSingleton<ValkeyLimitStore>();
        services.TryAddSingleton<ILimitStore>(provider => provider.GetRequiredService<ValkeyLimitStore>());
        services.TryAddSingleton<ILimiter, Limiter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReadinessCheck, ValkeyReadinessCheck>());
        return services;
    }

    /// <summary>The 415/413 guard for <c>POST /v1/chat/completions</c>. Add before authentication.</summary>
    public static IApplicationBuilder UseInferenceRequestGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<InferenceRequestGuardMiddleware>();
    }

    public static IEndpointRouteBuilder MapInferenceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return InferenceEndpoints.Map(app);
    }
}
