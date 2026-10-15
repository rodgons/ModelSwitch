using NetArchTest.Rules;

namespace ModelSwitch.ArchitectureTests;

public sealed class ModuleBoundaryTests
{
    [Theory]
    [MemberData(nameof(ModulePairData))]
    public void Module_does_not_reference_another_modules_non_contracts_namespaces(string from, string to)
    {
        string[] internalNamespaces =
        [
            $"ModelSwitch.{to}.Domain",
            $"ModelSwitch.{to}.Application",
            $"ModelSwitch.{to}.Infrastructure",
            $"ModelSwitch.{to}.Endpoints",
        ];

        var result = Types.InAssembly(ArchitectureModel.Modules[from])
            .ShouldNot()
            .HaveDependencyOnAny(internalNamespaces)
            .GetResult();

        ArchitectureModel.ShouldHaveNoFailures(result, $"{from} must only use {to} through its Contracts namespace");
    }

    [Theory]
    [MemberData(nameof(AdapterData))]
    public void Adapters_depend_only_on_adapters_abstractions_and_shared_kernel(string adapter)
    {
        var forbidden = ArchitectureModel.Modules.Keys
            .Select(m => $"ModelSwitch.{m}")
            .Concat(ArchitectureModel.HostNamespaces)
            .Concat(ArchitectureModel.Adapters.Keys
                .Where(a => a != adapter)
                .Select(a => $"ModelSwitch.Adapters.{a}"))
            .ToArray();

        var result = Types.InAssembly(ArchitectureModel.Adapters[adapter])
            .ShouldNot()
            .HaveDependencyOnAny(forbidden)
            .GetResult();

        ArchitectureModel.ShouldHaveNoFailures(result, $"Adapter {adapter} may only use Adapters.Abstractions and SharedKernel");
    }

    [Theory]
    [MemberData(nameof(ModuleData))]
    public void Domain_has_no_dependency_on_ef_core_aspnet_core_or_http_client(string module)
    {
        var result = Types.InAssembly(ArchitectureModel.Modules[module])
            .That()
            .ResideInNamespaceMatching(@"\.Domain(\.|$)")
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "System.Net.Http")
            .GetResult();

        ArchitectureModel.ShouldHaveNoFailures(result, $"{module}.Domain must stay free of EF Core, ASP.NET Core and HttpClient");
    }

    [Theory]
    [MemberData(nameof(ModuleData))]
    public void Endpoints_do_not_depend_on_infrastructure_or_ef_core(string module)
    {
        var result = Types.InAssembly(ArchitectureModel.Modules[module])
            .That()
            .ResideInNamespaceMatching(@"\.Endpoints(\.|$)")
            .ShouldNot()
            .HaveDependencyOnAny(ForbiddenForEndpoints())
            .GetResult();

        ArchitectureModel.ShouldHaveNoFailures(result, $"{module}.Endpoints must call Application or Contracts only");
    }

    [Fact]
    public void Api_host_does_not_depend_on_infrastructure_or_ef_core()
    {
        var result = Types.InAssembly(ArchitectureModel.ApiHost)
            .ShouldNot()
            .HaveDependencyOnAny(ForbiddenForEndpoints())
            .GetResult();

        ArchitectureModel.ShouldHaveNoFailures(result, "The Api host must not touch Infrastructure, DbContext or EF Core");
    }

    [Theory]
    [MemberData(nameof(ModuleData))]
    public void Only_contracts_and_the_module_entry_point_are_public(string module)
    {
        var result = Types.InAssembly(ArchitectureModel.Modules[module])
            .That()
            .DoNotResideInNamespaceMatching(@"\.Contracts(\.|$)")
            .And()
            .DoNotHaveName($"{module}Module")
            .ShouldNot()
            .BePublic()
            .GetResult();

        ArchitectureModel.ShouldHaveNoFailures(result, $"{module}: everything outside Contracts must be internal");
    }

    public static TheoryData<string, string> ModulePairData => ArchitectureModel.ModulePairCases();

    public static TheoryData<string> ModuleData => ArchitectureModel.ModuleCases();

    public static TheoryData<string> AdapterData => ArchitectureModel.AdapterCases();

    // Any DbContext lives in a module's Infrastructure namespace or derives from the EF Core type.
    private static string[] ForbiddenForEndpoints() =>
    [
        .. ArchitectureModel.Modules.Keys.Select(m => $"ModelSwitch.{m}.Infrastructure"),
        "Microsoft.EntityFrameworkCore",
    ];
}
