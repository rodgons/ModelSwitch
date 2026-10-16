using System.Text;
using System.Text.RegularExpressions;

namespace ModelSwitch.Evaluation.Domain.Metrics;

/// <summary>
/// Token-level F1 as the official SQuAD v2 evaluation script computes it (<c>normalize_answer</c> and <c>compute_f1</c>):
/// lowercase, remove ASCII punctuation, remove the articles a/an/the as whole words, split on whitespace, then F1 over the
/// multiset of shared tokens. When either side normalizes to no tokens the score is 1 if both do and 0 otherwise.
/// </summary>
internal static partial class TokenF1
{
    // Python's string.punctuation.
    private const string AsciiPunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    public static decimal Score(string prediction, string reference)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        ArgumentNullException.ThrowIfNull(reference);
        var predicted = Tokens(prediction);
        var gold = Tokens(reference);
        if (predicted.Length == 0 || gold.Length == 0)
        {
            return predicted.Length == gold.Length ? 1m : 0m;
        }

        var goldCounts = gold.GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var common = 0;
        foreach (var token in predicted)
        {
            if (goldCounts.TryGetValue(token, out var remaining) && remaining > 0)
            {
                goldCounts[token] = remaining - 1;
                common++;
            }
        }

        if (common == 0)
        {
            return 0m;
        }

        // 2PR/(P+R) with P = common/|pred| and R = common/|gold| simplifies to 2·common/(|pred|+|gold|).
        return 2m * common / (predicted.Length + gold.Length);
    }

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lower = text.ToLowerInvariant();
        var withoutPunctuation = new StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            if (!AsciiPunctuation.Contains(c, StringComparison.Ordinal))
            {
                withoutPunctuation.Append(c);
            }
        }

        var withoutArticles = Articles().Replace(withoutPunctuation.ToString(), " ");
        return string.Join(' ', Split(withoutArticles));
    }

    private static string[] Tokens(string text) => Split(Normalize(text));

    private static string[] Split(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"\b(a|an|the)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Articles();
}
