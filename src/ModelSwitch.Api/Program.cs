using ModelSwitch.Admin;
using ModelSwitch.Evaluation;
using ModelSwitch.Identity;
using ModelSwitch.Inference;
using ModelSwitch.Registry;
using ModelSwitch.Routing;
using ModelSwitch.Usage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIdentityModule();
builder.Services.AddRegistryModule();
builder.Services.AddRoutingModule();
builder.Services.AddInferenceModule();
builder.Services.AddUsageModule();
builder.Services.AddEvaluationModule();
builder.Services.AddAdminModule();

var app = builder.Build();

app.MapIdentityEndpoints();
app.MapRegistryEndpoints();
app.MapRoutingEndpoints();
app.MapInferenceEndpoints();
app.MapUsageEndpoints();
app.MapEvaluationEndpoints();
app.MapAdminEndpoints();

app.Run();
