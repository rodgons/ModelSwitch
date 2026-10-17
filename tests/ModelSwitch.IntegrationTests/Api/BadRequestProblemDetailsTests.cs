using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Http;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>
/// Regression: in the Compose deployment (environment Production) a malformed admin body, e.g. POST /admin/tenants with
/// <c>{bad</c>, answered an empty 400, because ASP.NET Core throws on binding failures only in Development, where every other
/// test runs. Validation errors must be 400 Problem Details everywhere (constitution). The client's mistake is not logged as
/// an unhandled server error either: that log's JSON reader message quotes the offending character of the body (ADR-0010).
/// </summary>
public sealed class BadRequestProblemDetailsTests
{
    private sealed record Body(string Name);

    [Theory]
    [InlineData("{bad")]
    [InlineData("{\"name\": 42}")]
    public async Task A_body_that_cannot_be_bound_is_problem_details_in_production(string body)
    {
        using var errors = new ErrorLog();
        await using var app = await StartAsync(errors);
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(
            new Uri("/things", UriKind.Relative),
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("\"errorCode\":\"invalid_request\"");
        errors.Entries.ShouldBeEmpty();
    }

    private static async Task<WebApplication> StartAsync(ErrorLog errors)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddProvider(errors);
        builder.Services.AddSharedKernel();
        builder.Services.AddBadRequestProblemDetails();
        var app = builder.Build();
        app.UseRequestIdAndErrorHandling();
        app.MapPost("/things", (Body body) => Results.Ok(body.Name));
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class ErrorLog : ILoggerProvider, ILogger
    {
        public List<string> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                lock (Entries)
                {
                    Entries.Add(formatter(state, exception));
                }
            }
        }

        public void Dispose()
        {
        }
    }
}
