using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.SharedKernel;
using Shouldly;

namespace ModelSwitch.UnitTests.SharedKernel;

public sealed class GuidGeneratorTests
{
    private static IGuidGenerator Create(FakeTimeProvider time)
        => new ServiceCollection().AddSingleton<TimeProvider>(time).AddSharedKernel().BuildServiceProvider().GetRequiredService<IGuidGenerator>();

    [Fact]
    public void Generates_version_7_ids()
    {
        var id = Create(new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture))).NewGuid();
        id.Version.ShouldBe(7);
    }

    [Fact]
    public void Ids_sort_by_creation_time()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var generator = Create(time);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(generator.NewGuid());
            time.Advance(TimeSpan.FromMilliseconds(5));
        }

        ids.Select(g => g.ToString()).ShouldBe(ids.Select(g => g.ToString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Embeds_the_provider_time()
    {
        var at = DateTimeOffset.Parse("2026-03-04T05:06:07.123Z", System.Globalization.CultureInfo.InvariantCulture);
        var id = Create(new FakeTimeProvider(at)).NewGuid();
        var hex = id.ToString("N")[..12];
        DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(hex, 16)).ShouldBe(at);
    }
}
