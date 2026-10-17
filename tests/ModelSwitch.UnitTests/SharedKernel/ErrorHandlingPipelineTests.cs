using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Http;
using Shouldly;

namespace ModelSwitch.UnitTests.SharedKernel;

/// <summary><see cref="HttpPipelineExtensions.UseRequestIdAndErrorHandling"/> on a real pipeline (the exception handler middleware).</summary>
public sealed class ErrorHandlingPipelineTests
{
    [Fact]
    public async Task An_unhandled_exception_answers_500_problem_details_that_still_carry_the_request_id_header()
    {
        // Regression (failure-injection "PostgreSQL stopped mid-load"): the exception handler clears the response, headers included,
        // before it re-executes, so the X-Request-Id set by RequestIdMiddleware was lost on exactly the responses that need it most.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IGuidGenerator>(new GuidGenerator(TimeProvider.System));
        await using var app = builder.Build();
        app.UseRequestIdAndErrorHandling();
        app.MapGet("/boom", string () => throw new InvalidOperationException("the database is down"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };

        using var response = await client.GetAsync(new Uri("/boom", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Headers.TryGetValues(RequestId.HeaderName, out var header).ShouldBeTrue("every response carries X-Request-Id (api.md section 1)");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("requestId").GetString().ShouldBe(header!.Single());
    }
}
