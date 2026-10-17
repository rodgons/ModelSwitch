using System.Net;
using System.Net.Sockets;
using ModelSwitch.Registry.Infrastructure.Outbound;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Registry;

public sealed class OutboundDestinationPolicyTests
{
    internal sealed class StubResolver : IDnsResolver
    {
        public Dictionary<string, Queue<IPAddress[]>> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Calls { get; private set; }

        public StubResolver Returns(string host, params string[][] successiveAnswers)
        {
            Answers[host] = new Queue<IPAddress[]>(successiveAnswers.Select(a => a.Select(IPAddress.Parse).ToArray()));
            return this;
        }

        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Calls++;
            if (!Answers.TryGetValue(host, out var queue))
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            // The last answer repeats, earlier ones are consumed (DNS rebinding: first answer public, then private).
            var answer = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return ValueTask.FromResult<IReadOnlyList<IPAddress>>(answer);
        }
    }

    private static OutboundDestinationPolicy Policy(StubResolver resolver, params string[] allowed)
        => new(new OutboundOptions(allowed), resolver);

    private static ValueTask<ModelSwitch.SharedKernel.Result> Validate(OutboundDestinationPolicy policy, string endpoint)
        => policy.ValidateEndpointAsync(endpoint, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://8.8.8.8/v1")]
    [InlineData("https://[2606:4700:4700::1111]/v1")]
    public async Task Public_https_endpoints_are_accepted(string endpoint)
    {
        var resolver = new StubResolver().Returns("api.openai.com", ["104.18.6.192"]);

        (await Validate(Policy(resolver), endpoint)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://127.0.0.1:8080")]
    [InlineData("https://[::1]/")]
    [InlineData("https://10.0.0.5/")]
    [InlineData("https://192.168.1.10/")]
    [InlineData("https://100.64.0.1/")]
    [InlineData("https://[fd00::1]/")]
    [InlineData("https://[::ffff:127.0.0.1]/")]
    [InlineData("https://0.0.0.0/")]
    public async Task Non_public_ip_literals_are_rejected(string endpoint)
    {
        var result = await Validate(Policy(new StubResolver()), endpoint);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCode.ValidationFailed);
        result.Error.FieldErrors!.ShouldContainKey("endpoint");
    }

    [Fact]
    public async Task Hostname_resolving_to_a_private_address_is_rejected()
    {
        var resolver = new StubResolver().Returns("internal.example.com", ["10.0.0.7"]);

        (await Validate(Policy(resolver), "https://internal.example.com")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Hostname_with_any_private_address_among_several_is_rejected()
    {
        var resolver = new StubResolver().Returns("mixed.example.com", ["8.8.8.8", "169.254.169.254"]);

        (await Validate(Policy(resolver), "https://mixed.example.com")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Localhost_is_rejected()
    {
        var resolver = new StubResolver().Returns("localhost", ["127.0.0.1", "::1"]);

        (await Validate(Policy(resolver), "https://localhost")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Unresolvable_host_is_rejected()
        => (await Validate(Policy(new StubResolver()), "https://nope.invalid")).IsFailure.ShouldBeTrue();

    [Theory]
    [InlineData("http://api.openai.com")]
    [InlineData("ftp://api.openai.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://api.openai.com")]
    [InlineData("api.openai.com")]
    [InlineData("")]
    [InlineData("https://user:pw@api.openai.com")]
    public async Task Bad_scheme_or_shape_is_rejected(string endpoint)
    {
        var resolver = new StubResolver().Returns("api.openai.com", ["104.18.6.192"]);

        (await Validate(Policy(resolver), endpoint)).IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("http://ollama:11434")]
    [InlineData("https://ollama:11434")]
    [InlineData("http://OLLAMA.:11434")]
    [InlineData("http://mock-provider/v1")]
    public async Task Allowlisted_hosts_may_use_http_and_skip_address_checks(string endpoint)
    {
        var resolver = new StubResolver().Returns("ollama", ["172.18.0.3"]).Returns("mock-provider", ["172.18.0.4"]);

        (await Validate(Policy(resolver, "ollama", "mock-provider"), endpoint)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Allowlist_does_not_cover_other_hosts_or_subdomains()
    {
        var resolver = new StubResolver().Returns("evil.ollama", ["10.0.0.1"]).Returns("ollama.evil.com", ["10.0.0.1"]);
        var policy = Policy(resolver, "ollama");

        (await Validate(policy, "http://evil.ollama")).IsFailure.ShouldBeTrue();
        (await Validate(policy, "https://ollama.evil.com")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Allowlisted_ip_literal_is_accepted()
        => (await Validate(Policy(new StubResolver(), "127.0.0.1"), "http://127.0.0.1:9000")).IsSuccess.ShouldBeTrue();

    [Fact]
    public async Task Dns_rebinding_is_caught_at_connect_time()
    {
        // Passes validation on save (public answer), then flips to the metadata address.
        var resolver = new StubResolver().Returns("rebind.example.com", ["8.8.8.8"], ["169.254.169.254"]);
        var policy = Policy(resolver);
        (await Validate(policy, "https://rebind.example.com")).IsSuccess.ShouldBeTrue();

        var connect = await OutboundDestinationPolicy.ResolveAndCheckAsync("rebind.example.com", resolver, TestContext.Current.CancellationToken);

        connect.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Connect_callback_refuses_a_rebound_host_and_never_opens_a_socket()
    {
        var resolver = new StubResolver().Returns("rebind.example.com", ["10.0.0.9"]);
        using var handler = new OutboundHandlerFactory(new OutboundOptions([]), resolver).CreatePrimaryHandler();
        using var client = new HttpClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.GetAsync("https://rebind.example.com/v1/models", TestContext.Current.CancellationToken));

        ex.Message.ShouldNotContain("10.0.0.9");
        resolver.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_rebinding_host_accepted_on_save_is_refused_by_the_real_handler_at_connect_time()
    {
        // testing.md section 4: the same resolver answers the save-time check (public) and the connection (metadata address).
        var resolver = new StubResolver().Returns("rebind.example.com", ["8.8.8.8"], ["169.254.169.254"]);
        var options = new OutboundOptions([]);
        (await Validate(new OutboundDestinationPolicy(options, resolver), "https://rebind.example.com")).IsSuccess.ShouldBeTrue();
        using var client = new HttpClient(new OutboundHandlerFactory(options, resolver).CreatePrimaryHandler());

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.GetAsync("https://rebind.example.com/v1/models", TestContext.Current.CancellationToken));

        ex.HttpRequestError.ShouldBe(HttpRequestError.ConnectionError);
        resolver.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_hostname_resolving_to_10_x_is_refused_on_save_and_at_connect_time()
    {
        var resolver = new StubResolver().Returns("intranet.example.com", ["10.1.2.3"]);
        var options = new OutboundOptions([]);
        using var client = new HttpClient(new OutboundHandlerFactory(options, resolver).CreatePrimaryHandler());

        (await Validate(new OutboundDestinationPolicy(options, resolver), "https://intranet.example.com")).IsFailure.ShouldBeTrue();
        await Should.ThrowAsync<HttpRequestException>(
            () => client.GetAsync("https://intranet.example.com/v1/models", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Connect_callback_refuses_loopback_literal_unless_allowlisted()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = ServeOnceAsync(listener);

        using (var blocked = new HttpClient(new OutboundHandlerFactory(new OutboundOptions([]), new StubResolver()).CreatePrimaryHandler()))
        {
            await Should.ThrowAsync<HttpRequestException>(
                () => blocked.GetAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken));
        }

        using var allowed = new HttpClient(new OutboundHandlerFactory(new OutboundOptions(["127.0.0.1"]), new StubResolver()).CreatePrimaryHandler());
        using var response = await allowed.GetAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
        await serve;
    }

    [Fact]
    public void Handler_disables_redirects_and_proxies()
    {
        using var handler = (SocketsHttpHandler)new OutboundHandlerFactory(new OutboundOptions([]), new StubResolver()).CreatePrimaryHandler();

        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.UseProxy.ShouldBeFalse();
        handler.PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(5));
    }

    private static async Task ServeOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
    }
}
