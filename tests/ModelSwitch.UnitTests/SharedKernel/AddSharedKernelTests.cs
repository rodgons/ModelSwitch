using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.SharedKernel;
using Shouldly;

namespace ModelSwitch.UnitTests.SharedKernel;

public sealed class AddSharedKernelTests
{
    [Fact]
    public void Registers_system_time_and_guid_generator()
    {
        using var provider = new ServiceCollection().AddSharedKernel().BuildServiceProvider();
        provider.GetRequiredService<TimeProvider>().ShouldBeSameAs(TimeProvider.System);
        provider.GetRequiredService<IGuidGenerator>().NewGuid().Version.ShouldBe(7);
    }
}
