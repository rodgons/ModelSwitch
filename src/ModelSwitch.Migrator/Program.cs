using Microsoft.Extensions.Hosting;
using ModelSwitch.SharedKernel;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSharedKernel();

using var host = builder.Build();

await host.StartAsync();
await host.StopAsync();
