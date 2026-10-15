using System.Text.Json;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// The use case behind <c>POST /v1/chat/completions</c>: read and validate the body, then hand the neutral request to
/// <see cref="IInferenceService"/>. The only thing the HTTP endpoint calls.
/// </summary>
internal sealed class ChatCompletionsService(InferenceOptions options, IInferenceService inference)
{
    public async Task<Result<InferenceResult>> CompleteAsync(Guid requestId, Guid tenantId, Stream body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            // The parser message can quote the offending bytes; send fixed text instead (ADR-0010).
            return new Error(ErrorCode.InvalidRequest, "The request body is not valid JSON.");
        }

        using (document)
        {
            var parsed = ChatRequestParser.Parse(document.RootElement, options);
            if (parsed.IsFailure)
            {
                return parsed.Error;
            }

            var request = new InferenceRequest(requestId, tenantId, parsed.Value.Alias, parsed.Value.Chat);
            return await inference.CompleteAsync(request, cancellationToken);
        }
    }
}
