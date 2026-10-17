using System.Net;
using System.Net.Sockets;
using ModelSwitch.SharedKernel.Hosting;
using Shouldly;

namespace ModelSwitch.UnitTests.SharedKernel;

public sealed class HealthProbeTests
{
    [Theory]
    [InlineData(null, "http://127.0.0.1:8080/health")]
    [InlineData("", "http://127.0.0.1:8080/health")]
    [InlineData("5000", "http://127.0.0.1:5000/health")]
    [InlineData("5000;6000", "http://127.0.0.1:5000/health")]
    [InlineData(" 7000 , 8000", "http://127.0.0.1:7000/health")]
    [InlineData("not-a-port", "http://127.0.0.1:8080/health")]
    [InlineData("70000", "http://127.0.0.1:8080/health")]
    public void Local_url_uses_the_first_configured_port(string? ports, string expected)
        => HealthProbe.LocalUrl(ports, "/health").ShouldBe(new Uri(expected));

    [Theory]
    [InlineData(new[] { "--health-check" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--health-check", "extra" }, false)]
    [InlineData(new[] { "seed" }, false)]
    public void Is_requested_only_by_the_lone_argument(string[] args, bool expected)
        => HealthProbe.IsRequested(args).ShouldBe(expected);

    [Fact]
    public async Task Nothing_listening_is_unhealthy()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var exitCode = await HealthProbe.RunAsync(new Uri($"http://127.0.0.1:{port}/health"), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
    }
}
