using ModelSwitch.Evaluation.Domain.Metrics;
using Shouldly;

namespace ModelSwitch.UnitTests.Evaluation;

/// <summary>SQuAD v2 evaluation script semantics (normalize_answer, compute_f1).</summary>
public sealed class TokenF1Tests
{
    [Fact]
    public void Normalization_lowercases_strips_punctuation_and_articles_and_collapses_whitespace()
        => TokenF1.Normalize("  The Quick,  brown fox! An  apple. A ").ShouldBe("quick brown fox apple");

    [Fact]
    public void Identical_answers_after_normalization_score_one()
        => TokenF1.Score("The Eiffel Tower.", "eiffel tower").ShouldBe(1m);

    [Fact]
    public void Partial_overlap_is_the_harmonic_mean_of_token_precision_and_recall()
    {
        // pred tokens [paris, france, capital], gold [paris]: P = 1/3, R = 1, F1 = 2*(1/3)/(4/3) = 0.5
        TokenF1.Score("Paris, France capital", "Paris").ShouldBe(0.5m);
    }

    [Fact]
    public void Repeated_tokens_count_by_multiset_intersection()
    {
        // pred [x, x, y], gold [x, y, y]: common = {x:1, y:1} = 2, P = 2/3, R = 2/3, F1 = 2/3
        TokenF1.Score("x x y", "x y y").ShouldBe(2m / 3m, 0.0000001m);
    }

    [Fact]
    public void No_common_tokens_score_zero()
        => TokenF1.Score("London", "Paris").ShouldBe(0m);

    [Fact]
    public void Both_empty_after_normalization_score_one_and_one_empty_scores_zero()
    {
        TokenF1.Score("The.", "a").ShouldBe(1m);
        TokenF1.Score("", "Paris").ShouldBe(0m);
        TokenF1.Score("Paris", "the").ShouldBe(0m);
    }

    [Fact]
    public void Articles_are_removed_only_as_whole_words()
        => TokenF1.Normalize("Theater and anthem").ShouldBe("theater and anthem");
}
