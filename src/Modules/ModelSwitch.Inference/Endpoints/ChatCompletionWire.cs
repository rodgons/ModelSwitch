using System.Text.Json.Nodes;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Inference.Application;
using ModelSwitch.Inference.Contracts;

namespace ModelSwitch.Inference.Endpoints;

/// <summary>The OpenAI-compatible JSON bodies of the inference API (docs/api.md section 1).</summary>
internal static class ChatCompletionWire
{
    public static JsonObject ChatCompletion(InferenceResult result, long created)
    {
        var usage = result.Usage;
        var prompt = usage?.InputTokens ?? 0;
        var completion = usage?.OutputTokens ?? 0;

        // estimated_cost is added with the cost calculator in Phase 2.
        return new JsonObject
        {
            ["id"] = $"chatcmpl-{result.RequestId}",
            ["object"] = "chat.completion",
            ["created"] = created,
            ["model"] = result.Alias,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = result.Text },
                ["finish_reason"] = FinishReasonText(result.FinishReason),
            }),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = prompt,
                ["completion_tokens"] = completion,
                ["total_tokens"] = prompt + completion,
            },
            ["modelswitch"] = new JsonObject
            {
                ["request_id"] = result.RequestId.ToString(),
                ["usage_source"] = usage is null ? "unavailable" : "reported",
                ["attempts"] = result.Attempts,
            },
        };
    }

    public static JsonObject ModelList(IReadOnlyList<ModelAliasInfo> models) => new()
    {
        ["object"] = "list",
        ["data"] = new JsonArray([.. models.Select(m => (JsonNode)new JsonObject
        {
            ["id"] = m.Id,
            ["object"] = "model",
            ["created"] = m.Created,
            ["owned_by"] = "modelswitch",
        })]),
    };

    internal static string FinishReasonText(FinishReason reason) => reason switch
    {
        FinishReason.Length => "length",
        FinishReason.ContentFilter => "content_filter",
        _ => "stop",
    };
}
