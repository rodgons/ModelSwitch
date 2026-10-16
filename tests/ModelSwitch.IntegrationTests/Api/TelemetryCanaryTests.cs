using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelSwitch.IntegrationTests.Infrastructure;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>
/// security.md section 6 and DoD #11: a full request carrying a canary prompt, sent with a Provider whose credential is a canary
/// secret, leaves neither canary in any exported span, metric or log line. The api runs with the real telemetry setup and in-memory
/// exporters added to it.
/// </summary>
public sealed class TelemetryCanaryTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    private const string _credentialVariable = "MS_TEST_CANARY_PROVIDER_KEY";

    [Fact]
    public async Task No_span_metric_or_log_line_contains_the_prompt_or_a_secret()
    {
        var canarySecret = $"sk-canary-secret-{Guid.CreateVersion7():N}";
        var canaryPrompt = $"canary-prompt-{Guid.CreateVersion7():N}";
        Environment.SetEnvironmentVariable(_credentialVariable, canarySecret);
        await gateway.WriteConfigurationAsync($"update registry.provider set credential_reference = 'env:{_credentialVariable}' where name = 'mock-openai'");

        var spans = new List<Activity>();
        var metrics = new List<Metric>();
        var logs = new List<LogRecord>();
        var lines = new CapturingLoggerProvider();
        await using var host = gateway.StartHost().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(spans));
            services.ConfigureOpenTelemetryMeterProvider(meters => meters.AddInMemoryExporter(metrics));
            services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddInMemoryExporter(logs));

            // Every ordinary logger sink (the JSON console among them) gets what this provider gets: message, state and scopes.
            services.AddSingleton<ILoggerProvider>(lines);
        }));
        using var client = gateway.ClientFor(host);

        var completion = await client.PostAsync("/v1/chat/completions", Body(canaryPrompt, stream: false), TestContext.Current.CancellationToken);
        var stream = await client.PostAsync("/v1/chat/completions", Body(canaryPrompt, stream: true), TestContext.Current.CancellationToken);

        // The canary really went through: the echo model returned it, so the test is not vacuous.
        completion.StatusCode.ShouldBe(HttpStatusCode.OK);
        stream.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await completion.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain(canaryPrompt);
        (await stream.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("[DONE]");
        var requestId = completion.Headers.GetValues("X-Request-Id").Single();

        // Settlement and the request log line run after the response; wait for the last one, then flush everything.
        await WaitForAsync(() => logs.ToArray().Count(l => l.EventId.Id == 4001) >= 2);
        host.Services.GetRequiredService<TracerProvider>().ForceFlush();
        host.Services.GetRequiredService<MeterProvider>().ForceFlush();
        host.Services.GetRequiredService<LoggerProvider>().ForceFlush();

        var exported = Render(spans.ToArray(), metrics.ToArray(), logs.ToArray()) + lines.Text;
        lines.Text.ShouldContain(requestId, Case.Sensitive, "the plain log lines carry the request id scope");

        // What must be there: the request path spans, the request metrics and the completion line, correlated by request id.
        spans.ShouldContain(s => s.OperationName == "modelswitch.attempt");
        spans.ShouldContain(s => s.OperationName == "modelswitch.settle");
        metrics.ShouldContain(m => m.Name == "modelswitch.inference.requests");
        metrics.ShouldContain(m => m.Name == "modelswitch.tokens");
        exported.ShouldContain(requestId);

        // What must never be there.
        exported.ShouldNotContain(canaryPrompt);
        exported.ShouldNotContain(canarySecret);
        exported.ShouldNotContain(gateway.FullKey);
    }

    /// <summary>A logger sink that keeps, as text, every line with its state and the scopes open when it was written.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public string Text => string.Join('\n', _lines);

        public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class Capturing(CapturingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => provider._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var line = new StringBuilder().Append(category).Append(' ').Append(formatter(state, exception)).Append(' ').Append(exception);
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var (key, value) in pairs)
                    {
                        line.Append(' ').Append(key).Append('=').Append(value);
                    }
                }

                provider._scopes.ForEachScope((scope, builder) => builder.Append(" scope=").Append(scope is IEnumerable<KeyValuePair<string, object?>> kv ? string.Join(',', kv) : scope), line);
                provider._lines.Enqueue(line.ToString());
            }
        }
    }

    private static StringContent Body(string prompt, bool stream)
        => new(
            new JsonObject
            {
                ["model"] = GatewayFixture.ChatAlias,
                ["stream"] = stream,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            }.ToJsonString(),
            Encoding.UTF8,
            "application/json");

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20);
        }

        condition().ShouldBeTrue();
    }

    // Everything an exporter would send, as text: names, tags, events, status, metric point tags and values, log messages, attributes and scopes.
    private static string Render(Activity[] spans, Metric[] metrics, LogRecord[] logs)
    {
        var text = new StringBuilder();
        foreach (var span in spans)
        {
            text.Append(span.OperationName).Append(' ').Append(span.DisplayName).Append(' ').Append(span.StatusDescription).AppendLine();
            foreach (var tag in span.TagObjects)
            {
                text.Append(tag.Key).Append('=').Append(tag.Value).AppendLine();
            }

            foreach (var spanEvent in span.Events)
            {
                text.Append(spanEvent.Name).AppendLine();
                foreach (var tag in spanEvent.Tags)
                {
                    text.Append(tag.Key).Append('=').Append(tag.Value).AppendLine();
                }
            }

            foreach (var baggage in span.Baggage)
            {
                text.Append(baggage.Key).Append('=').Append(baggage.Value).AppendLine();
            }
        }

        foreach (var metric in metrics)
        {
            text.Append(metric.Name).AppendLine();
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                foreach (var tag in point.Tags)
                {
                    text.Append(tag.Key).Append('=').Append(tag.Value).AppendLine();
                }
            }
        }

        foreach (var log in logs)
        {
            text.Append(log.CategoryName).Append(' ').Append(log.FormattedMessage).Append(' ').Append(log.Body).Append(' ').Append(log.Exception).AppendLine();
            foreach (var attribute in log.Attributes ?? [])
            {
                text.Append(attribute.Key).Append('=').Append(attribute.Value).AppendLine();
            }

            log.ForEachScope(
                static (scope, builder) =>
                {
                    foreach (var item in scope)
                    {
                        builder.Append(item.Key).Append('=').Append(item.Value).AppendLine();
                    }
                },
                text);
        }

        return text.ToString();
    }
}
