using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelSwitch.Inference.Application;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Inference.Endpoints;

namespace ModelSwitch.Inference;

public static class InferenceModule
{
    public static IServiceCollection AddInferenceModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(provider => InferenceOptions.From(provider.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton<IInferenceService, InferenceService>();
        services.TryAddSingleton<ChatCompletionsService>();
        services.TryAddSingleton<ModelListService>();
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
