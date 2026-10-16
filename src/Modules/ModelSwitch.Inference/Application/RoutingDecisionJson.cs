using System.Buffers;
using System.Text;
using System.Text.Json;
using ModelSwitch.Routing.Contracts;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// The Routing Decision as recorded on <c>inference_request.routing_decision</c> (data-model.md): the strategy, the matched
/// rule, the ordered Candidates and the exclusions with their reasons. Provider Model ids only: no names, no content (ADR-0010).
/// A request pinned to one Provider Model is recorded with <c>"strategy": "pinned"</c> and that model as the only Candidate.
/// </summary>
internal static class RoutingDecisionJson
{
    public static string Write(RoutingDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            // A pinned Provider Model consulted no policy: "pinned" stands where the policy's strategy would.
            writer.WriteString("strategy", decision.Pinned ? "pinned" : decision.Strategy.ToString().ToLowerInvariant());
            if (decision.RuleMatched is { } rule)
            {
                writer.WriteNumber("ruleMatched", rule);
            }
            else
            {
                writer.WriteNull("ruleMatched");
            }

            writer.WriteNumber("maxOutputTokens", decision.EffectiveMaxOutputTokens);
            writer.WriteStartArray("ordered");
            foreach (var candidate in decision.Ordered)
            {
                writer.WriteStringValue(candidate.ProviderModelId);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("excluded");
            foreach (var excluded in decision.Excluded)
            {
                writer.WriteStartObject();
                writer.WriteString("model", excluded.ProviderModelId);
                writer.WriteString("reason", excluded.Reason);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
