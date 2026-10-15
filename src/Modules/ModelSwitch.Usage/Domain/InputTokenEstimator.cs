using System.Text;
using Microsoft.ML.Tokenizers;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Usage.Domain;

internal sealed class InputTokenEstimator : IInputTokenEstimator
{
    public const string O200kBase = "o200k_base";
    public const string Cl100kBase = "cl100k_base";
    public const string BytesDiv3 = "bytes_div_3";

    private const int _windowChars = 1024;

    // Building a Tiktoken vocabulary is expensive, so each is built once, on first use.
    private static readonly Lazy<TiktokenTokenizer> _o200k = new(() => TiktokenTokenizer.CreateForEncoding(O200kBase));
    private static readonly Lazy<TiktokenTokenizer> _cl100k = new(() => TiktokenTokenizer.CreateForEncoding(Cl100kBase));

    public InputTokenEstimate Estimate(IReadOnlyList<string> messageTexts, string? tokenizer)
    {
        ArgumentNullException.ThrowIfNull(messageTexts);

        var overhead = (long)messageTexts.Count * IInputTokenEstimator.PerMessageOverhead;
        var encoding = tokenizer switch
        {
            O200kBase => _o200k.Value,
            Cl100kBase => _cl100k.Value,
            _ => null,
        };

        if (encoding is null)
        {
            long bytes = 0;
            foreach (var text in messageTexts)
            {
                bytes += Encoding.UTF8.GetByteCount(text);
            }

            return new InputTokenEstimate(Clamp((bytes + 2) / 3 + overhead), BytesDiv3);
        }

        long tokens = 0;
        foreach (var text in messageTexts)
        {
            tokens += CountBounded(encoding, text);
        }

        return new InputTokenEstimate(Clamp(tokens + overhead), $"tokenizer:{tokenizer}");
    }

    // Byte-pair merging is quadratic in the length of an unbroken run (80 000 characters of 'x' take over 3 s, a 1 MiB body would
    // take minutes), and a client controls the text. Counting in bounded windows makes the cost linear. A window edge can split
    // a word, which changes the count by at most a token per window: an estimate, as documented.
    private static long CountBounded(TiktokenTokenizer encoding, string text)
    {
        long tokens = 0;
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            var length = Math.Min(remaining.Length, _windowChars);
            if (length < remaining.Length && char.IsHighSurrogate(remaining[length - 1]))
            {
                length--;
            }

            tokens += encoding.CountTokens(remaining[..length]);
            remaining = remaining[length..];
        }

        return tokens;
    }

    private static int Clamp(long value) => (int)Math.Min(value, int.MaxValue);
}
