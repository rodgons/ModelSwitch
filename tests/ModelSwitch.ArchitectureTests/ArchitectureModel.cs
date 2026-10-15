using System.Reflection;
using ModelSwitch.Admin;
using ModelSwitch.Evaluation;
using ModelSwitch.Identity;
using ModelSwitch.Inference;
using ModelSwitch.Registry;
using ModelSwitch.Routing;
using ModelSwitch.Usage;
using Shouldly;

namespace ModelSwitch.ArchitectureTests;

/// <summary>The modules, adapters and hosts under test, loaded through marker types or by name.</summary>
internal static class ArchitectureModel
{
    public static readonly IReadOnlyDictionary<string, Assembly> Modules = new Dictionary<string, Assembly>
    {
        ["Identity"] = typeof(IdentityModule).Assembly,
        ["Registry"] = typeof(RegistryModule).Assembly,
        ["Routing"] = typeof(RoutingModule).Assembly,
        ["Inference"] = typeof(InferenceModule).Assembly,
        ["Usage"] = typeof(UsageModule).Assembly,
        ["Evaluation"] = typeof(EvaluationModule).Assembly,
        ["Admin"] = typeof(AdminModule).Assembly,
    };

    // Adapters and the Api host have no public marker type, so they are loaded by name.
    // They are project references of this test project, so they are always copied next to it.
    public static readonly IReadOnlyDictionary<string, Assembly> Adapters = new Dictionary<string, Assembly>
    {
        ["OpenAICompatible"] = Assembly.Load("ModelSwitch.Adapters.OpenAICompatible"),
        ["Ollama"] = Assembly.Load("ModelSwitch.Adapters.Ollama"),
    };

    public static readonly Assembly ApiHost = Assembly.Load("ModelSwitch.Api");

    public static TheoryData<string> ModuleCases()
    {
        var data = new TheoryData<string>();
        foreach (var name in Modules.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    public static TheoryData<string, string> ModulePairCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var from in Modules.Keys)
        {
            foreach (var to in Modules.Keys.Where(to => to != from))
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    public static TheoryData<string> AdapterCases()
    {
        var data = new TheoryData<string>();
        foreach (var name in Adapters.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    public static void ShouldHaveNoFailures(NetArchTest.Rules.TestResult result, string rule)
    {
        var failing = result.FailingTypeNames ?? [];
        failing.ShouldBeEmpty($"{rule}. Offending types: {string.Join(", ", failing)}");
    }
}
