using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ModelSwitch.Evaluation;

public static class EvaluationModule
{
    public static IServiceCollection AddEvaluationModule(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapEvaluationEndpoints(this IEndpointRouteBuilder app) => app;
}
