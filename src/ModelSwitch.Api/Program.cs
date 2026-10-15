using Microsoft.AspNetCore.Authentication;
using ModelSwitch.Admin;
using ModelSwitch.Api.Authentication;
using ModelSwitch.Evaluation;
using ModelSwitch.Identity;
using ModelSwitch.Inference;
using ModelSwitch.Registry;
using ModelSwitch.Routing;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Hosting;
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
        builder.Services.AddRoutingModule();
        builder.Services.AddInferenceModule();
        builder.Services.AddUsageModule();
        builder.Services.AddEvaluationModule();
        builder.Services.AddAdminModule();

        builder.Services
            .AddAuthentication(ApiKeyDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, configureOptions: null);
        builder.Services.AddAuthorization();

        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();

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
