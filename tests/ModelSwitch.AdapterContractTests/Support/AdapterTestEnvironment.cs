using Microsoft.Extensions.Logging;

namespace ModelSwitch.AdapterContractTests.Support;

/// <summary>What the suite hands an Adapter factory: an HTTP client (no resilience handlers, ADR-0004), a capturing logger factory and a clock.</summary>
public sealed record AdapterTestEnvironment(HttpClient Http, ILoggerFactory LoggerFactory, TimeProvider TimeProvider);

/// <summary>Collects every log message an Adapter writes so the suite can assert no content or credential leaked (ADR-0010).</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private void Add(string entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var structured = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(' ', pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            owner.Add($"{formatter(state, exception)} {structured} {exception}");
        }
    }
}
