using System.Text.Json;
using ModelSwitch.Evaluation.Domain.Evaluators;
using ModelSwitch.Evaluation.Domain.Templates;
using Shouldly;

namespace ModelSwitch.UnitTests.Evaluation;

/// <summary>Keeps <c>samples/evaluations</c> usable: every prompt parses, every evaluator config is valid, every Case renders.</summary>
public sealed class SampleEvaluationsTests
{
    private static readonly string _samples = Path.Combine(RepositoryRoot(), "samples", "evaluations");

    [Theory]
    [InlineData("extraction-contacts")]
    [InlineData("classification-support-intents")]
    public void Every_sample_case_renders_with_its_prompt_and_its_evaluator_config_is_valid(string name)
    {
        var template = PromptTemplate.Parse(Read($"{name}.prompt.json")).Value;
        var evaluator = Read($"{name}.evaluator.json");
        var created = EvaluatorRegistry.Create(evaluator.GetProperty("kind").GetString()!, evaluator.GetProperty("version").GetString()!, evaluator.GetProperty("config"));
        created.IsSuccess.ShouldBeTrue();

        var lines = File.ReadAllLines(Path.Combine(_samples, $"{name}.jsonl"));
        lines.Length.ShouldBe(20);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var @case = JsonDocument.Parse(line).RootElement;
            ids.Add(@case.GetProperty("id").GetString()!).ShouldBeTrue();
            PromptRenderer.Render(template, @case.GetProperty("input")).IsSuccess.ShouldBeTrue();

            // A perfect answer must score 1, so the sample's expected results fit its evaluator.
            var expected = @case.GetProperty("expected");
            var perfect = expected.ValueKind == JsonValueKind.String ? expected.GetString()! : expected.GetRawText();
            ((ILocalEvaluator)created.Value).Score(new CaseContext(@case.GetProperty("input"), expected, perfect)).Score.ShouldBe(1m);
        }
    }

    [Fact]
    public void Sample_emails_use_only_reserved_example_domains()
    {
        var text = File.ReadAllText(Path.Combine(_samples, "extraction-contacts.jsonl"));
        var domains = System.Text.RegularExpressions.Regex.Matches(text, @"@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+)")
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .ToArray();

        domains.ShouldNotBeEmpty();
        domains.ShouldAllBe(d => d == "example.com" || d == "example.org" || d == "example.net");
    }

    private static JsonElement Read(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(_samples, file))).RootElement.Clone();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ModelSwitch.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
