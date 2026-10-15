using Microsoft.Extensions.Options;

namespace ModelSwitch.SharedKernel.Hosting;

/// <summary>Maps invalid deployment configuration to exit code 78 (EX_CONFIG), one line per problem (FR-10).</summary>
public static class StartupExit
{
    public const int ConfigurationError = 78;

    /// <summary>
    /// Runs <paramref name="run"/>. If it throws because options validation failed, writes each failure to
    /// <paramref name="error"/> and returns <see cref="ConfigurationError"/>; any other exception propagates.
    /// </summary>
    public static async Task<int> RunAsync(Func<Task<int>> run, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            return await run();
        }
        catch (Exception exception) when (TryCollectFailures(exception, out var failures))
        {
            foreach (var failure in failures)
            {
                await error.WriteLineAsync($"configuration error: {failure}");
            }

            return ConfigurationError;
        }
    }

    private static bool TryCollectFailures(Exception exception, out List<string> failures)
    {
        failures = [];
        Collect(exception, failures);
        return failures.Count > 0;
    }

    private static void Collect(Exception exception, List<string> failures)
    {
        switch (exception)
        {
            case OptionsValidationException validation:
                failures.AddRange(validation.Failures);
                break;
            case AggregateException aggregate:
                foreach (var inner in aggregate.InnerExceptions)
                {
                    Collect(inner, failures);
                }

                break;
        }
    }
}
