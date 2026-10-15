using Microsoft.AspNetCore.Authentication;
using ModelSwitch.Adapters.Ollama;
using ModelSwitch.Adapters.OpenAICompatible;
using ModelSwitch.Admin;
using ModelSwitch.Api.Authentication;
using ModelSwitch.Api.Endpoints;
using ModelSwitch.Evaluation;
using ModelSwitch.Identity;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Inference;
using ModelSwitch.Registry;
using ModelSwitch.Routing;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Http;
using ModelSwitch.Usage;

// Invalid deployment configuration exits with code 78 and names the variable (FR-10).
return await StartupExit.RunAsync(
    async () =>
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddEnvironmentVariables(RequiredSetting.EnvironmentPrefix);

        builder.Services.AddSharedKernel();
        builder.Services.AddIdentityModule();
        builder.Services.AddRegistryModule();
        builder.Services.AddProviderAdapter<OllamaAdapter>(OllamaAdapter.TypeName);
        // Per-Provider OpenAICompatibleOptions are not in the registry schema yet, so the Adapter runs with its defaults.
        builder.Services.AddProviderAdapter<OpenAICompatibleAdapter>(OpenAICompatibleAdapter.TypeName);
        builder.Services.AddRoutingModule();
        builder.Services.AddInferenceModule();
        builder.Services.AddUsageModule();
        builder.Services.AddEvaluationModule();
        builder.Services.AddAdminModule();

        builder.Services
            .AddAuthentication(ApiKeyDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, configureOptions: null);
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(ApiKeyClaims.InferencePolicy, policy => policy
                .AddAuthenticationSchemes(ApiKeyDefaults.Scheme)
                .RequireClaim(ApiKeyClaims.Scope, ApiKeyScopes.Inference))
            .AddPolicy(ApiKeyClaims.ModelsReadPolicy, policy => policy
                .AddAuthenticationSchemes(ApiKeyDefaults.Scheme)
                .RequireClaim(ApiKeyClaims.Scope, ApiKeyScopes.ModelsRead, ApiKeyScopes.Inference));

        var app = builder.Build();

        app.UseRequestIdAndErrorHandling();
        app.UseInferenceRequestGuard();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealthEndpoints();
        app.MapIdentityEndpoints();
        app.MapRegistryEndpoints();
        app.MapRoutingEndpoints();
        app.MapInferenceEndpoints();
        app.MapUsageEndpoints();
        app.MapEvaluationEndpoints();
        app.MapAdminEndpoints();

        await app.RunAsync();
        return 0;
    },
    Console.Error);
