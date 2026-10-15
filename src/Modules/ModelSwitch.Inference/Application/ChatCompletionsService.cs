using System.Text.Json;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// The use case behind <c>POST /v1/chat/completions</c>: read and validate the body, then hand the neutral request to
/// <see cref="IInferenceService"/>. The only thing the HTTP endpoint calls.
/// </summary>
internal sealed record ChatCaller(Guid RequestId, Guid TenantId, Guid? ApiKeyId, string? ClientRequestId)
{
    /// <summary>Raw <c>X-ModelSwitch-Task</c>.</summary>
    public string? TaskType { get; init; }

    /// <summary>Raw <c>X-ModelSwitch-Timeout-Ms</c>.</summary>
    public string? TimeoutMs { get; init; }
}

/// <summary>Validates the ModelSwitch request headers (api.md section 1): <c>X-ModelSwitch-Task</c> and <c>X-ModelSwitch-Timeout-Ms</c>.</summary>
internal static partial class RoutingHeaders
{
    public const string TaskHeaderName = "X-ModelSwitch-Task";
    public const string TimeoutHeaderName = "X-ModelSwitch-Timeout-Ms";

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9_-]{1,64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex TaskPattern();

    public static Result<string?> ValidateTask(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Result.Success<string?>(null);
        }

        return TaskPattern().IsMatch(value)
            ? Result.Success<string?>(value)
            : Invalid<string?>(TaskHeaderName, "X-ModelSwitch-Task must match [a-z0-9_-]{1,64}.");
    }

    /// <summary>A positive number of milliseconds. It can only shorten the Deadline, which the caller enforces with <c>min</c>.</summary>
    public static Result<TimeSpan?> ValidateTimeout(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Result.Success<TimeSpan?>(null);
        }

        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0
            ? Result.Success<TimeSpan?>(TimeSpan.FromMilliseconds(milliseconds))
            : Invalid<TimeSpan?>(TimeoutHeaderName, "X-ModelSwitch-Timeout-Ms must be a positive number of milliseconds.");
    }

    private static Result<T> Invalid<T>(string header, string message)
        => Result.Failure<T>(new Error(ErrorCode.InvalidRequest, message, new Dictionary<string, string[]> { [header] = [message] }));
}

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

/// <summary>The answer to a chat completions call: a finished completion, or a stream whose First Token has arrived.</summary>
/// <param name="Completion">Set for a non-streaming call.</param>
/// <param name="Stream">Set for <c>stream: true</c>.</param>
/// <param name="IncludeUsage">Whether the client asked for the final usage chunk.</param>
internal sealed record ChatReply(InferenceResult? Completion, StreamingCompletion? Stream, bool IncludeUsage);

internal sealed class ChatCompletionsService(InferenceOptions options, IInferenceService inference)
{
    public async Task<Result<ChatReply>> CompleteAsync(ChatCaller caller, Stream body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(body);

        var clientRequestId = ClientRequestIds.Validate(caller.ClientRequestId);
        if (clientRequestId.IsFailure)
        {
            return clientRequestId.Error;
        }

        var taskType = RoutingHeaders.ValidateTask(caller.TaskType);
        if (taskType.IsFailure)
        {
            return taskType.Error;
        }

        var timeout = RoutingHeaders.ValidateTimeout(caller.TimeoutMs);
        if (timeout.IsFailure)
        {
            return timeout.Error;
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
                TaskType = taskType.Value,
                Timeout = timeout.Value,
                InputBytes = (int)Math.Min(counted.BytesRead, int.MaxValue),
            };
            if (parsed.Value.Stream)
            {
                var stream = await inference.StreamAsync(request, cancellationToken);
                return stream.IsSuccess ? new ChatReply(null, stream.Value, parsed.Value.IncludeUsage) : stream.Error;
            }

            var completion = await inference.CompleteAsync(request, cancellationToken);
            return completion.IsSuccess ? new ChatReply(completion.Value, null, IncludeUsage: false) : completion.Error;
        }
    }
}
