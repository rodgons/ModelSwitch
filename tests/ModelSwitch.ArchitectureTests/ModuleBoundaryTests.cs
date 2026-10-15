using NetArchTest.Rules;
using Shouldly;

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
        // An allowlist, which NetArchTest's prefix matching cannot express, so it checks assembly references:
        // any new ModelSwitch project an Adapter starts using fails here.
        string[] allowed = ["ModelSwitch.Adapters.Abstractions", "ModelSwitch.SharedKernel"];

        var disallowed = ArchitectureModel.Adapters[adapter]
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith("ModelSwitch.", StringComparison.Ordinal) && !allowed.Contains(name))
            .ToList();

        disallowed.ShouldBeEmpty($"Adapter {adapter} may only use Adapters.Abstractions and SharedKernel. Offending references: {string.Join(", ", disallowed)}");
    }

    [Theory]
    [MemberData(nameof(AdapterData))]
    public void Adapters_do_not_depend_on_aspnet_core(string adapter)
    {
        var result = Types.InAssembly(ArchitectureModel.Adapters[adapter])
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        ArchitectureModel.ShouldHaveNoFailures(result, $"Adapter {adapter} must stay free of ASP.NET Core");

        var frameworkReferences = ArchitectureModel.Adapters[adapter]
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToList();
        frameworkReferences.ShouldBeEmpty($"Adapter {adapter} must not reference ASP.NET Core assemblies");
    }

    [Fact]
    public void Shared_kernel_does_not_reference_aspnet_core()
    {
        var offending = typeof(ModelSwitch.SharedKernel.Result).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToList();

        offending.ShouldBeEmpty("SharedKernel must stay free of ASP.NET Core; HTTP helpers live in SharedKernel.Http");
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
            // EF Core scaffolds migrations as public classes; they are generated, so they are exempt.
            .DoNotResideInNamespaceMatching(@"\.Infrastructure\.Migrations(\.|$)")
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
