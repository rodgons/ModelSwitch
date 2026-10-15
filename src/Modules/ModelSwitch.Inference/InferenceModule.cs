using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ModelSwitch.Inference;

public static class InferenceModule
{
    public static IServiceCollection AddInferenceModule(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapInferenceEndpoints(this IEndpointRouteBuilder app) => app;
}
