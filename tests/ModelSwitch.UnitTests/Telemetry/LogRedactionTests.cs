using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelSwitch.SharedKernel.Telemetry;
using ModelSwitch.Telemetry;
using Shouldly;

namespace ModelSwitch.UnitTests.Telemetry;

/// <summary>security.md section 6: classified values are erased from log lines, whatever provider writes them.</summary>
public sealed partial class LogRedactionTests
{
    [Fact]
    public void Secret_and_content_parameters_are_erased_and_ordinary_ones_kept()
    {
        var capture = new CapturingProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            TelemetryHostingExtensions.AddLogRedaction(logging, services);
            logging.AddProvider(capture);
        });
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<LogRedactionTests>>();

        Classified(logger, "sk-provider-secret", "the user's prompt", "req-123");

        var line = capture.Lines.ShouldHaveSingleItem();
        line.ShouldNotContain("sk-provider-secret");
        line.ShouldNotContain("the user's prompt");
        line.ShouldContain("Secret= Content= Request=req-123");
    }

    [Theory]
    [InlineData(null, 1.0)]
    [InlineData("0.1", 0.1)]
    [InlineData("0", 0.0)]
    [InlineData("1.5", 1.0)]
    [InlineData("not-a-number", 1.0)]
    public void The_trace_sample_ratio_defaults_to_one_and_rejects_values_outside_zero_to_one(string? configured, double expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TelemetryHostingExtensions.SampleRatioSetting] = configured })
            .Build();

        TelemetryHostingExtensions.SampleRatio(configuration).ShouldBe(expected);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Secret={Secret} Content={Content} Request={Request}")]
    private static partial void Classified(ILogger logger, [SecretData] string secret, [ContentData] string content, string request);

    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(Lines);

        public void Dispose()
        {
        }

        private sealed class Capturing(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Add(formatter(state, exception));
        }
    }
}
