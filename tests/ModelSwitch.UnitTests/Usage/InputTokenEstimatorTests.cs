using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Usage;

public sealed class InputTokenEstimatorTests
{
    private readonly InputTokenEstimator _estimator = new();

    [Fact]
    public void Counts_with_the_o200k_tokenizer_plus_four_tokens_per_message()
    {
        // "hello world" is two tokens in o200k_base.
        var estimate = _estimator.Estimate(["hello world", "hello world"], "o200k_base");

        estimate.Tokens.ShouldBe(2 + 2 + (2 * IInputTokenEstimator.PerMessageOverhead));
        estimate.Method.ShouldBe("tokenizer:o200k_base");
    }

    [Fact]
    public void Counts_with_the_cl100k_tokenizer()
    {
        var estimate = _estimator.Estimate(["hello world"], "cl100k_base");

        estimate.Tokens.ShouldBe(2 + IInputTokenEstimator.PerMessageOverhead);
        estimate.Method.ShouldBe("tokenizer:cl100k_base");
    }

    [Fact]
    public void The_two_encodings_can_differ()
    {
        const string text = "日本語のテキストを数えます。 Unicode text, émigré naïveté.";

        var o200k = _estimator.Estimate([text], "o200k_base").Tokens;
        var cl100k = _estimator.Estimate([text], "cl100k_base").Tokens;

        o200k.ShouldBeLessThan(cl100k, "o200k has the larger vocabulary and packs non-English text tighter");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("llama3")]
    public void Without_a_known_tokenizer_it_uses_ceil_of_utf8_bytes_over_three(string? tokenizer)
    {
        // 10 ASCII bytes -> ceil(10 / 3) = 4, plus the overhead of the one message.
        var estimate = _estimator.Estimate(["0123456789"], tokenizer);

        estimate.Tokens.ShouldBe(4 + IInputTokenEstimator.PerMessageOverhead);
        estimate.Method.ShouldBe("bytes_div_3");
    }

    [Fact]
    public void Byte_fallback_counts_utf8_bytes_not_characters()
    {
        // Three CJK characters are 9 UTF-8 bytes -> 3 tokens, plus overhead.
        _estimator.Estimate(["日本語"], null).Tokens.ShouldBe(3 + IInputTokenEstimator.PerMessageOverhead);
    }

    [Fact]
    public void Byte_fallback_rounds_up_over_the_whole_request_not_per_message()
    {
        // Three one-byte messages are 3 bytes -> 1 token (per message it would be 3).
        _estimator.Estimate(["a", "b", "c"], null).Tokens.ShouldBe(1 + (3 * IInputTokenEstimator.PerMessageOverhead));
    }

    [Fact]
    public void An_empty_message_still_pays_the_overhead()
    {
        _estimator.Estimate([""], "o200k_base").Tokens.ShouldBe(IInputTokenEstimator.PerMessageOverhead);
    }

    [Fact]
    public void No_messages_is_zero_tokens()
    {
        _estimator.Estimate([], "o200k_base").Tokens.ShouldBe(0);
    }

    [Fact]
    public void The_fallback_does_not_underestimate_ascii_text_relative_to_the_tokenizer()
    {
        var text = string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 50));

        var tokenizer = _estimator.Estimate([text], "o200k_base").Tokens;
        var fallback = _estimator.Estimate([text], null).Tokens;

        fallback.ShouldBeGreaterThanOrEqualTo(tokenizer);
    }

    // Regression: byte-pair merging is quadratic in an unbroken run, so one 1 MiB word used to burn minutes of CPU per request.
    // Flake fix: this used to assert a 10 s wall-clock ceiling, but the first call also paid the one-time (static Lazy) build of the
    // o200k vocabulary, which under full-suite CPU load blew the ceiling. Now the vocabulary is warmed first and the property is
    // asserted as scaling, which load slows equally on both sides: 64x the input must cost far less than the 4096x a quadratic
    // algorithm would (linear: ~64x). The 500x bound leaves a wide margin for noise either way.
    [Fact]
    public void An_unbroken_megabyte_is_counted_in_linear_time()
    {
        _estimator.Estimate(["warm up"], "o200k_base");
        var small = new string('x', 16 * 1024);
        var large = new string('x', 1024 * 1024);

        var smallTime = Enumerable.Range(0, 3).Min(_ => Time(small));
        var largeTime = Time(large);

        (largeTime.TotalMilliseconds / Math.Max(smallTime.TotalMilliseconds, 0.1)).ShouldBeLessThan(500);
        var estimate = _estimator.Estimate([large], "o200k_base");
        estimate.Tokens.ShouldBeGreaterThan(1000);
        estimate.Method.ShouldBe("tokenizer:o200k_base");

        TimeSpan Time(string text)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _estimator.Estimate([text], "o200k_base");
            return watch.Elapsed;
        }
    }

    [Fact]
    public void Windowing_does_not_split_a_surrogate_pair()
    {
        // 1023 ASCII characters then an emoji (a surrogate pair) straddling the 1024-character window edge.
        var text = new string('a', 1023) + "\U0001F600" + new string('b', 10);

        Should.NotThrow(() => _estimator.Estimate([text], "o200k_base"));
    }
}
