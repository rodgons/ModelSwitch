using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ModelSwitch.SharedKernel.Hosting;
using Shouldly;

namespace ModelSwitch.UnitTests.SharedKernel;

public sealed class RequiredSettingTests
{
    [Fact]
    public void Environment_variable_name_uses_the_prefix_and_double_underscores()
    {
        RequiredSetting.EnvironmentVariableFor("Security:ApiKeyPepper").ShouldBe("MODELSWITCH__Security__ApiKeyPepper");
    }

    [Fact]
    public async Task Missing_setting_exits_78_and_names_the_variable()
    {
        var (exit, error) = await RunAsync(configuration: [], b => b.Services.RequireSetting("ConnectionStrings:Postgres"));

        exit.ShouldBe(78);
        error.ShouldContain("MODELSWITCH__ConnectionStrings__Postgres");
    }

    [Fact]
    public async Task Invalid_setting_exits_78_with_the_reason()
    {
        var (exit, error) = await RunAsync(
            [new("Security:ApiKeyPepper", "short")],
            b => b.Services.RequireSetting("Security:ApiKeyPepper", _ => "too short."));

        exit.ShouldBe(78);
        error.ShouldContain("MODELSWITCH__Security__ApiKeyPepper");
        error.ShouldContain("too short.");
    }

    [Fact]
    public async Task Every_problem_is_reported_one_per_line()
    {
        var (exit, error) = await RunAsync(
            configuration: [],
            b => b.Services.RequireSetting("A:One").RequireSetting("B:Two"));

        exit.ShouldBe(78);
        error.ShouldContain("MODELSWITCH__A__One");
        error.ShouldContain("MODELSWITCH__B__Two");
    }

    [Fact]
    public async Task The_same_setting_required_twice_is_reported_once()
    {
        var (_, error) = await RunAsync(
            configuration: [],
            b => b.Services.RequireSetting("A:One").RequireSetting("A:One"));

        error.Split("MODELSWITCH__A__One").Length.ShouldBe(2);
    }

    [Fact]
    public async Task Valid_settings_start_normally()
    {
        var (exit, error) = await RunAsync(
            [new("A:One", "value")],
            b => b.Services.RequireSetting("A:One", v => v == "value" ? null : "wrong"));

        exit.ShouldBe(0);
        error.ShouldBeEmpty();
    }

    private static async Task<(int Exit, string Error)> RunAsync(
        KeyValuePair<string, string?>[] configuration,
        Action<HostApplicationBuilder> configure)
    {
        var error = new StringWriter();
        var exit = await StartupExit.RunAsync(
            async () =>
            {
                var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
                builder.Configuration.AddInMemoryCollection(configuration);
                configure(builder);
                using var host = builder.Build();
                await host.StartAsync();
                await host.StopAsync();
                return 0;
            },
            error);
        return (exit, error.ToString());
    }
}
