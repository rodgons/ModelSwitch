using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace ModelSwitch.UnitTests.Telemetry;

/// <summary>
/// Regression (benchmarks/2026-10-17-266c696.md, finding 4): with no <c>Logging</c> section, EF Core logged every DB command and
/// HttpClient four lines per Provider call at Information, about ten lines per request. The hosts' appsettings.json raise those
/// categories to Warning and keep the gateway's own lines (one per completed request) at Information.
/// </summary>
public sealed class LogLevelDefaultsTests
{
    [Theory]
    [InlineData("src/ModelSwitch.Api/appsettings.json")]
    [InlineData("src/ModelSwitch.Worker/appsettings.json")]
    public void Framework_chatter_is_warning_and_the_gateway_logs_at_information(string path)
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(RepositoryRoot(), path), optional: false).Build();
        using var services = new ServiceCollection()
            .AddLogging(logging => logging.AddConfiguration(configuration.GetSection("Logging")).AddProvider(new EnabledProvider()))
            .BuildServiceProvider();
        var loggers = services.GetRequiredService<ILoggerFactory>();

        loggers.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").IsEnabled(LogLevel.Information).ShouldBeFalse();
        loggers.CreateLogger("System.Net.Http.HttpClient.OpenAICompatible.LogicalHandler").IsEnabled(LogLevel.Information).ShouldBeFalse();
        loggers.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").IsEnabled(LogLevel.Warning).ShouldBeTrue();
        loggers.CreateLogger("ModelSwitch.Inference.Application.InferenceService").IsEnabled(LogLevel.Information).ShouldBeTrue();
        loggers.CreateLogger("Microsoft.Hosting.Lifetime").IsEnabled(LogLevel.Information).ShouldBeTrue();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ModelSwitch.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class EnabledProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Enabled();

        public void Dispose()
        {
        }

        private sealed class Enabled : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
            }
        }
    }
}
