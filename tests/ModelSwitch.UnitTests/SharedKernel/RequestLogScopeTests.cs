using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Http;
using Shouldly;

namespace ModelSwitch.UnitTests.SharedKernel;

/// <summary>Every log line inside a request carries RequestId, and TenantId once authentication found it (observability.md section 4).</summary>
public sealed partial class RequestLogScopeTests
{
    [Fact]
    public async Task Lines_inside_a_request_carry_the_request_id_and_the_tenant_set_after_authentication()
    {
        var logger = new ScopeCapturingLogger();
        var tenantId = Guid.CreateVersion7();
        var context = new DefaultHttpContext();
        var middleware = new RequestIdMiddleware(
            http =>
            {
                Line(logger, "before authentication");
                RequestId.SetTenant(http, tenantId);
                Line(logger, "after authentication");
                return Task.CompletedTask;
            },
            new GuidGenerator(TimeProvider.System),
            logger);

        await middleware.InvokeAsync(context);

        var requestId = RequestId.Get(context);
        logger.Lines[0].ShouldBe(new Dictionary<string, object?> { ["RequestId"] = requestId });
        logger.Lines[1].ShouldBe(new Dictionary<string, object?> { ["RequestId"] = requestId, ["TenantId"] = tenantId });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Text}")]
    private static partial void Line(ILogger logger, string text);

    /// <summary>Records, for each line, the key/value pairs of the scopes open when it was written.</summary>
    private sealed class ScopeCapturingLogger : ILogger<RequestIdMiddleware>
    {
        private readonly Stack<object> _scopes = new();

        public List<Dictionary<string, object?>> Lines { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            _scopes.Push(state);
            return new Pop(_scopes);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>();
            foreach (var scope in _scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>())
            {
                foreach (var (key, value) in scope)
                {
                    values[key] = value;
                }
            }

            Lines.Add(values);
        }

        private sealed class Pop(Stack<object> scopes) : IDisposable
        {
            public void Dispose() => scopes.Pop();
        }
    }
}
