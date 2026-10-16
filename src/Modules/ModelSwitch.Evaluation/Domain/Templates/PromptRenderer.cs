using System.Text.Json;
using ModelSwitch.Evaluation.Domain.Evaluators;

namespace ModelSwitch.Evaluation.Domain.Templates;

/// <summary>The messages for one Case, ready to send, and the template's <c>responseFormat</c> unchanged.</summary>
internal sealed record RenderedPrompt(IReadOnlyList<PromptMessage> Messages, JsonElement? ResponseFormat);

/// <summary>Either a <see cref="RenderedPrompt"/> or the <see cref="CaseError"/> that makes the Case errored.</summary>
internal sealed record RenderResult(RenderedPrompt? Prompt, CaseError? Error)
{
    public bool IsSuccess => Prompt is not null;
}

/// <summary>
/// Turns a Prompt Version template and a Case's <c>input</c> object into messages, by plain-text substitution with no logic.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A string value is inserted as its raw text (no quotes, no escaping).</item>
/// <item>Any other value (number, boolean, null, object, array) is inserted as compact JSON, e.g. <c>4.50</c>, <c>true</c>,
/// <c>null</c>, <c>{"k":"v"}</c>. A present <c>null</c> is therefore the text <c>null</c>, not a missing variable.</item>
/// <item>A variable absent from the input is a Case error <c>template_variable_missing</c>, never an empty string.</item>
/// <item>Substitution is one pass: text that a value brings in is not scanned for placeholders again.</item>
/// </list>
/// </remarks>
internal static class PromptRenderer
{
    public static RenderResult Render(PromptTemplate template, JsonElement input)
    {
        ArgumentNullException.ThrowIfNull(template);
        var missing = template.Variables
            .Where(name => input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(name, out _))
            .ToArray();
        if (missing.Length > 0)
        {
            return new RenderResult(
                null,
                new CaseError(
                    CaseErrorCodes.TemplateVariableMissing,
                    $"The Case input has no value for template variable(s): {string.Join(", ", missing)}."));
        }

        var messages = template.Messages
            .Select(m => m with { Content = PromptTemplate.Placeholder().Replace(m.Content, match => EvaluationJson.Text(input.GetProperty(match.Groups["name"].Value))) })
            .ToArray();
        return new RenderResult(new RenderedPrompt(messages, template.ResponseFormat), null);
    }
}
