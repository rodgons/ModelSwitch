using System.Diagnostics;
using Shouldly;
using Xunit.Sdk;

namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>
/// The one way a test waits for something another thread, process or host does (testing.md section 1): poll a condition on a short
/// interval and fail with <c>because</c> once the deadline passes. Never a fixed <c>Task.Delay</c> sized to "long enough", which
/// races under full-suite load.
/// </summary>
public static class Eventually
{
    private static readonly TimeSpan _interval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Returns as soon as <paramref name="condition"/> is true. An exception from the condition counts as "not yet" (a container may
    /// still be starting) and is reported in the failure if the deadline passes. A failed assertion inside the condition, or cancelling
    /// the test, ends the wait at once.
    /// </summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan within, string because)
    {
        var token = TestContext.Current.CancellationToken;
        var clock = Stopwatch.StartNew();
        Exception? last = null;
        while (true)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is not (ShouldAssertException or IAssertionException) && !token.IsCancellationRequested)
            {
                last = exception;
            }

            if (clock.Elapsed >= within)
            {
                var cause = last is null ? string.Empty : $" (last error: {last.GetType().Name}: {last.Message})";
                Assert.Fail($"Not true within {within.TotalSeconds:0.###} s: {because}{cause}");
            }

            await Task.Delay(_interval, token);
        }
    }

    /// <inheritdoc cref="UntilAsync(Func{Task{bool}}, TimeSpan, string)"/>
    public static Task UntilAsync(Func<bool> condition, TimeSpan within, string because)
        => UntilAsync(() => Task.FromResult(condition()), within, because);
}
