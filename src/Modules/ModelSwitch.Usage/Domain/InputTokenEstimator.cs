using System.Text;
using Microsoft.ML.Tokenizers;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Usage.Domain;

internal sealed class InputTokenEstimator : IInputTokenEstimator
{
    public const string O200kBase = "o200k_base";
    public const string Cl100kBase = "cl100k_base";
    public const string BytesDiv3 = "bytes_div_3";

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
            tokens += encoding.CountTokens(text);
        }

        return new InputTokenEstimate(Clamp(tokens + overhead), $"tokenizer:{tokenizer}");
    }

    private static int Clamp(long value) => (int)Math.Min(value, int.MaxValue);
}
