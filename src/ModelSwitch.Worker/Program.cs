using ModelSwitch.Evaluation;
using ModelSwitch.Identity;
using ModelSwitch.Inference;
using ModelSwitch.Registry;
using ModelSwitch.Routing;
using ModelSwitch.Usage;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddUsageModule();
builder.Services.AddEvaluationModule();
builder.Services.AddRegistryModule();
builder.Services.AddInferenceModule();
builder.Services.AddIdentityModule();
builder.Services.AddRoutingModule();

await builder.Build().RunAsync();
