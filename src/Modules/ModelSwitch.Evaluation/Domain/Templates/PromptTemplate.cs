using System.Text.Json;
using System.Text.RegularExpressions;
using ModelSwitch.Evaluation.Domain.Evaluators;
using ModelSwitch.SharedKernel;

namespace ModelSwitch.Evaluation.Domain.Templates;

/// <summary>One chat message: a role (<c>system</c>, <c>user</c> or <c>assistant</c>) and its text.</summary>
internal sealed record PromptMessage(string Role, string Content);

/// <summary>
/// The template of a Prompt Version (evaluation.md section 2, stored as <c>prompt_version.template</c>): chat messages whose
/// content may hold <c>{{variable}}</c> placeholders, plus an optional <c>responseFormat</c> passed through to the request as
/// written. A placeholder is two braces around a name (<c>[A-Za-z_][A-Za-z0-9_]*</c>), with optional spaces inside the braces;
/// anything else between braces is ordinary text.
/// </summary>
internal sealed partial record PromptTemplate(IReadOnlyList<PromptMessage> Messages, JsonElement? ResponseFormat)
{
    private static readonly HashSet<string> _roles = new(StringComparer.Ordinal) { "system", "user", "assistant" };

    /// <summary>The distinct variable names the messages refer to, in order of first use.</summary>
    public IReadOnlyList<string> Variables { get; } = Messages
        .SelectMany(m => Placeholder().Matches(m.Content).Select(match => match.Groups["name"].Value))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>Validates a template body; errors are keyed by path (<c>messages[1].role</c>) for a 400 <c>validation_failed</c>.</summary>
    public static Result<PromptTemplate> Parse(JsonElement template)
    {
        var reader = new JsonConfigReader(template, string.Empty);
        var messages = new List<PromptMessage>();
        var list = reader.Property("messages");
        if (list is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() == 0)
        {
            reader.AddError("messages", "Must be a non-empty array of messages.");
        }
        else
        {
            var i = 0;
            foreach (var item in array.EnumerateArray())
            {
                var message = reader.Nested(item, $"messages[{i}]");
                var role = message.String("role", required: true);
                if (role is not null && !_roles.Contains(role))
                {
                    message.AddError(message.PathOf("role"), "Must be system, user or assistant.");
                }

                var content = message.Property("content");
                if (content is not { ValueKind: JsonValueKind.String })
                {
                    message.AddError(message.PathOf("content"), "Must be a string.");
                }

                message.RejectUnknown();
                if (role is not null && content is { ValueKind: JsonValueKind.String } text)
                {
                    messages.Add(new PromptMessage(role, text.GetString()!));
                }

                i++;
            }
        }

        var responseFormat = reader.Property("responseFormat");
        if (responseFormat is { ValueKind: not JsonValueKind.Object })
        {
            reader.AddError("responseFormat", "Must be an object.");
        }

        return reader.Finish(() => new PromptTemplate(messages, responseFormat?.Clone()), "The prompt template is invalid.");
    }

    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.CultureInvariant)]
    internal static partial Regex Placeholder();
}
