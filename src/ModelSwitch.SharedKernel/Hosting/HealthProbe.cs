using System.Globalization;

namespace ModelSwitch.SharedKernel.Hosting;

/// <summary>
/// The container health check (docs/deployment.md section 1). The chiseled runtime images have no shell, curl or wget, so the
/// image's <c>HEALTHCHECK</c> runs the app's own entry point with <see cref="Argument"/>: it GETs a local health path and exits
/// 0 on a 2xx answer, 1 otherwise, without building the host.
/// </summary>
public static class HealthProbe
{
    public const string Argument = "--health-check";

    /// <summary>ASP.NET Core's default port in the .NET container images (<c>ASPNETCORE_HTTP_PORTS</c> unset).</summary>
    public const int DefaultPort = 8080;

    public static bool IsRequested(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count == 1 && string.Equals(args[0], Argument, StringComparison.Ordinal);
    }

    /// <summary>The loopback URL of <paramref name="path"/> on the first port in <c>ASPNETCORE_HTTP_PORTS</c> (<c>;</c> or <c>,</c> separated).</summary>
    public static Uri LocalUrl(string? httpPorts, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var first = (httpPorts ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var port = int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= 65535
            ? parsed
            : DefaultPort;
        return new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{path}");
    }

    /// <summary>Returns the process exit code: 0 when <paramref name="url"/> answers 2xx within <paramref name="timeout"/>, else 1.</summary>
    public static async Task<int> RunAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var client = new HttpClient { Timeout = timeout };
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }

    /// <summary>Probes <paramref name="path"/> on this container's own HTTP port.</summary>
    public static Task<int> RunAsync(string path)
        => RunAsync(LocalUrl(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"), path), TimeSpan.FromSeconds(3), CancellationToken.None);
}
