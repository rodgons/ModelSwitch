using System.Text.Json;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// The use case behind <c>POST /v1/chat/completions</c>: read and validate the body, then hand the neutral request to
/// <see cref="IInferenceService"/>. The only thing the HTTP endpoint calls.
/// </summary>
internal sealed record ChatCaller(Guid RequestId, Guid TenantId, Guid? ApiKeyId, string? ClientRequestId);

/// <summary>Validates <c>X-Client-Request-Id</c>: at most 128 printable ASCII characters (api.md section 1).</summary>
internal static class ClientRequestIds
{
    public const int MaxLength = 128;
    public const string HeaderName = "X-Client-Request-Id";
    private const string _message = "X-Client-Request-Id must be at most 128 printable ASCII characters.";

    public static Result<string?> Validate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Result.Success<string?>(null);
        }

        return value.Length <= MaxLength && value.All(c => c is >= ' ' and <= '~')
            ? Result.Success<string?>(value)
            : Result.Failure<string?>(new Error(ErrorCode.InvalidRequest, _message, new Dictionary<string, string[]> { [HeaderName] = [_message] }));
    }
}

/// <summary>Counts the bytes read, so the request body size can be recorded without buffering the body.</summary>
internal sealed class CountingStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        BytesRead += read;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class ChatCompletionsService(InferenceOptions options, IInferenceService inference)
{
    public async Task<Result<InferenceResult>> CompleteAsync(ChatCaller caller, Stream body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(body);

        var clientRequestId = ClientRequestIds.Validate(caller.ClientRequestId);
        if (clientRequestId.IsFailure)
        {
            return clientRequestId.Error;
        }

        var counted = new CountingStream(body);

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(counted, cancellationToken: cancellationToken);
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

            var request = new InferenceRequest(caller.RequestId, caller.TenantId, parsed.Value.Alias, parsed.Value.Chat)
            {
                ApiKeyId = caller.ApiKeyId,
                ClientRequestId = clientRequestId.Value,
                InputBytes = (int)Math.Min(counted.BytesRead, int.MaxValue),
            };
            return await inference.CompleteAsync(request, cancellationToken);
        }
    }
}
