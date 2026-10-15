using System.Text.Json;
using System.Text.Json.Nodes;
using ModelSwitch.Adapters.Abstractions;

namespace ModelSwitch.AdapterContractTests.Support;

/// <summary>A successful non-streaming completion to put on the wire.</summary>
public sealed record CompletionStub(string Text, FinishReason FinishReason, NeutralUsage? Usage);

/// <summary>A successful streamed completion to put on the wire. A null <see cref="Usage"/> means the Provider reports none.</summary>
public sealed record StreamStub(IReadOnlyList<string> Deltas, FinishReason FinishReason, NeutralUsage? Usage);

/// <summary>The kind of error object a Provider embeds in a 200 response.</summary>
public enum InnerErrorKind
{
    /// <summary>A rate limit (transient).</summary>
    RateLimit,

    /// <summary>A billing or quota error (permanent).</summary>
    Quota,

    /// <summary>An error with nothing classifiable inside (transient).</summary>
    Unspecified,
}

/// <summary>
/// Describes one Provider's wire format to the shared contract suite. A concrete Adapter's test subclass supplies one of these
/// (usually backed by its recorded fixtures) plus an Adapter factory, and gets the whole suite.
/// All <c>Encode*</c> methods return what the Provider would put on the wire; the suite decides when to send it.
/// </summary>
public abstract class ProviderWireFormat
{
    /// <summary>Path prefix of <see cref="AdapterContext.Endpoint"/> (e.g. <c>/v1</c>). Empty when the endpoint is the server root.</summary>
    public virtual string EndpointPath => string.Empty;

    /// <summary>Absolute request path for chat completions (e.g. <c>/v1/chat/completions</c>).</summary>
    public abstract string ChatPath { get; }

    /// <summary>Absolute path <see cref="IProviderAdapter.CheckAsync"/> calls.</summary>
    public abstract string CheckPath { get; }

    /// <summary>Absolute path <see cref="IProviderAdapter.ListModelsAsync"/> calls.</summary>
    public abstract string ListModelsPath { get; }

    /// <summary>The <c>Content-Type</c> of a streamed response (<c>text/event-stream</c> or <c>application/x-ndjson</c>).</summary>
    public abstract string StreamContentType { get; }

    /// <summary>The header that carries the credential. The secret must appear only here.</summary>
    public virtual string AuthHeaderName => "Authorization";

    /// <summary>The body of a successful non-streaming response.</summary>
    public abstract string EncodeCompletion(CompletionStub stub);

    /// <summary>The frames (SSE events or NDJSON lines, each complete with its terminator) that carry the given deltas.</summary>
    public abstract IReadOnlyList<string> EncodeStreamDeltas(IReadOnlyList<string> deltas);

    /// <summary>The closing frames: finish reason, optional usage, and the end marker (<c>[DONE]</c> for SSE).</summary>
    public abstract IReadOnlyList<string> EncodeStreamEnd(FinishReason finishReason, NeutralUsage? usage);

    /// <summary>The body of a non-2xx response. <paramref name="providerErrorCode"/> is the Provider's own error code, when it has one.</summary>
    public abstract string EncodeError(string? providerErrorCode, string message);

    /// <summary>A 200 non-streaming body that carries an error object instead of a completion.</summary>
    public abstract string EncodeErrorInOkBody(InnerErrorKind kind, string message);

    /// <summary>A single stream frame that carries an error object.</summary>
    public abstract string EncodeErrorInStreamFrame(InnerErrorKind kind, string message);

    /// <summary>A 200 non-streaming body that is not valid JSON.</summary>
    public abstract string EncodeMalformedBody();

    /// <summary>A stream frame whose payload is not valid JSON.</summary>
    public abstract string EncodeMalformedFrame();

    /// <summary>Asserts the request asks for <c>json_object</c> output in this Provider's format.</summary>
    public abstract void AssertJsonObjectRequest(JsonNode requestBody);

    /// <summary>Asserts the request asks for schema-constrained output in this Provider's format.</summary>
    public abstract void AssertJsonSchemaRequest(JsonNode requestBody, string name, JsonElement schema, bool strict);

    /// <summary>The body of a successful connectivity check.</summary>
    public virtual string EncodeCheckBody() => "{}";

    /// <summary>The body of a model listing that contains exactly <paramref name="modelIds"/>.</summary>
    public abstract string EncodeModelList(IReadOnlyList<string> modelIds);

    /// <summary>
    /// A 400 body saying the prompt exceeds the context window, or null when the Provider has no distinguishable error for it.
    /// An Adapter must classify it permanent with <see cref="ProviderFailureCodes.ContextLengthExceeded"/>.
    /// </summary>
    public virtual string? EncodeContextLengthExceeded() => null;

    /// <summary>
    /// Whether an error object embedded in a 200 response carries enough information to tell a rate limit from a quota error.
    /// When false, only <see cref="InnerErrorKind.Unspecified"/> is exercised.
    /// </summary>
    public virtual bool InnerErrorsAreClassifiable => true;

    /// <summary>All frames of a complete successful stream.</summary>
    public IReadOnlyList<string> EncodeStream(StreamStub stub) =>
        [.. EncodeStreamDeltas(stub.Deltas), .. EncodeStreamEnd(stub.FinishReason, stub.Usage)];
}
