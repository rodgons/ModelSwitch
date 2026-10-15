using System.Buffers;
using System.IO.Pipelines;

namespace ModelSwitch.Adapters.OpenAICompatible.Wire;

internal enum SseKind
{
    /// <summary>A <c>data:</c> line was read; its payload is in <see cref="SseReader.Payload"/>.</summary>
    Data,

    /// <summary>The <c>data: [DONE]</c> sentinel.</summary>
    Done,

    /// <summary>The server closed the stream.</summary>
    End,
}

/// <summary>
/// Reads Server-Sent Events line by line from a <see cref="PipeReader"/> without per-event allocations: the payload of the current
/// event is copied into a reused buffer. Each <c>data:</c> line is its own event (OpenAI never splits a payload over lines); comment
/// lines (<c>:</c>, such as OpenRouter's keep-alives), blank lines and the other SSE fields are skipped.
/// </summary>
internal sealed class SseReader(PipeReader pipe)
{
    private static ReadOnlySpan<byte> DataPrefix => "data:"u8;

    private static ReadOnlySpan<byte> DoneSentinel => "[DONE]"u8;

    private byte[] _line = new byte[1024];
    private int _payloadStart;
    private int _payloadLength;

    /// <summary>The payload of the last <see cref="SseKind.Data"/> event. Valid until the next read.</summary>
    public ReadOnlySpan<byte> Payload => _line.AsSpan(_payloadStart, _payloadLength);

    public async ValueTask<SseKind> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var result = await pipe.ReadAsync(ct);
            var buffer = result.Buffer;

            while (buffer.PositionOf((byte)'\n') is { } newline)
            {
                var line = buffer.Slice(0, newline);
                buffer = buffer.Slice(buffer.GetPosition(1, newline));
                if (TryTakeData(line, out var kind))
                {
                    pipe.AdvanceTo(buffer.Start);
                    return kind;
                }
            }

            if (result.IsCompleted)
            {
                // A final line without a terminating newline.
                var kind = SseKind.End;
                if (!buffer.IsEmpty && TryTakeData(buffer, out var last))
                {
                    kind = last;
                }

                pipe.AdvanceTo(buffer.End);
                return kind;
            }

            pipe.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private bool TryTakeData(ReadOnlySequence<byte> line, out SseKind kind)
    {
        kind = SseKind.Data;
        var length = checked((int)line.Length);
        if (_line.Length < length)
        {
            _line = new byte[Math.Max(length, _line.Length * 2)];
        }

        line.CopyTo(_line);
        if (length > 0 && _line[length - 1] == (byte)'\r')
        {
            length--;
        }

        var span = _line.AsSpan(0, length);
        if (!span.StartsWith(DataPrefix))
        {
            return false;
        }

        var start = DataPrefix.Length;
        if (start < length && span[start] == (byte)' ')
        {
            start++;
        }

        _payloadStart = start;
        _payloadLength = length - start;
        if (Payload.SequenceEqual(DoneSentinel))
        {
            kind = SseKind.Done;
        }

        return true;
    }
}
