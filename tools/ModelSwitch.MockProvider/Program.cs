using ModelSwitch.MockProvider;
using ModelSwitch.SharedKernel.Hosting;

// The container HEALTHCHECK (docs/deployment.md section 1): the chiseled image has no curl, so the app probes itself.
if (HealthProbe.IsRequested(args))
{
    return await HealthProbe.RunAsync("/healthz");
}

await MockProviderApp.Build(args).RunAsync();
return 0;
