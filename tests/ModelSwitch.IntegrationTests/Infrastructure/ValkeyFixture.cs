using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(ModelSwitch.IntegrationTests.Infrastructure.ValkeyFixture))]

namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>
/// One Valkey container per test assembly (testing.md section 1), the same image the deployment runs. Tests that share it use
/// their own Tenants, so counters never collide. A test that stops or restarts Valkey starts a container of its own with
/// <see cref="StartDedicatedAsync"/> instead.
/// </summary>
public sealed class ValkeyFixture : IAsyncLifetime
{
    public const string Image = "valkey/valkey:8";

    private readonly RedisContainer _container = Create();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>A started Valkey container that the caller owns and may stop.</summary>
    public static async Task<RedisContainer> StartDedicatedAsync()
    {
        var container = Create();
        await container.StartAsync();
        return container;
    }

    private static RedisContainer Create() => new RedisBuilder(Image).Build();
}
