using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Inference.Application;
using ModelSwitch.Registry.Infrastructure.Health;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

/// <summary>
/// Every row of the failure-classification table in docs/routing-and-resilience.md section 3, as the attempt loop and the
/// circuit breaker act on it (testing.md section 6 requires a test per row). What each Adapter maps a wire failure to is
/// covered by the Adapter contract suite; this is what happens next.
/// </summary>
public sealed class FailureClassificationTableTests
{
    private static readonly (string Situation, FailureClass Class, string Code, bool Retry, bool Fallback, bool Counts)[] _rows =
    [
        // situation, class, adapter code, retry same model, fall back, counts toward the circuit
        ("Connect, DNS or TLS failure (nothing sent)", FailureClass.Transient, ProviderFailureCodes.ConnectFailed, true, true, true),
        ("HTTP 408", FailureClass.Transient, ProviderFailureCodes.RequestTimeout, true, true, true),
        ("HTTP 409", FailureClass.Transient, ProviderFailureCodes.Conflict, true, true, true),
        ("HTTP 425", FailureClass.Transient, ProviderFailureCodes.TooEarly, true, true, true),
        ("HTTP 429", FailureClass.Transient, ProviderFailureCodes.RateLimited, true, true, true),
        ("HTTP 429 billing/quota code, and HTTP 402", FailureClass.Permanent, ProviderFailureCodes.QuotaExhausted, false, true, true),
        ("HTTP 500, 502, 503, 504, 529 and other 5xx", FailureClass.Transient, ProviderFailureCodes.ServerError, true, true, true),
        ("HTTP 524 (edge timeout)", FailureClass.Ambiguous, ProviderFailureCodes.EdgeTimeout, false, true, true),
        ("Attempt timeout before response headers", FailureClass.Ambiguous, ProviderFailureCodes.Timeout, false, true, true),
        ("Connection reset after the request was sent", FailureClass.Ambiguous, ProviderFailureCodes.ConnectionReset, false, true, true),
        ("HTTP 400", FailureClass.Permanent, ProviderFailureCodes.BadRequest, false, false, false),
        ("HTTP 404", FailureClass.Permanent, ProviderFailureCodes.NotFound, false, false, false),
        ("HTTP 413", FailureClass.Permanent, ProviderFailureCodes.PayloadTooLarge, false, false, false),
        ("HTTP 422", FailureClass.Permanent, ProviderFailureCodes.Unprocessable, false, false, false),
        ("context_length_exceeded", FailureClass.Permanent, ProviderFailureCodes.ContextLengthExceeded, false, true, false),
        ("HTTP 401", FailureClass.Permanent, ProviderFailureCodes.Unauthorized, false, true, true),
        ("HTTP 403", FailureClass.Permanent, ProviderFailureCodes.Forbidden, false, true, true),
        ("Any other 4xx", FailureClass.Permanent, ProviderFailureCodes.ClientError, false, false, false),
        ("Malformed or unparseable success body", FailureClass.Transient, ProviderFailureCodes.InvalidProviderResponse, true, true, true),
    ];

    public static TheoryData<string, FailureClass, string, bool, bool, bool> Rows()
    {
        var data = new TheoryData<string, FailureClass, string, bool, bool, bool>();
        foreach (var row in _rows)
        {
            data.Add(row.Situation, row.Class, row.Code, row.Retry, row.Fallback, row.Counts);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Each_row_of_the_table_is_retried_fallen_back_from_and_counted_as_documented(
        string situation, FailureClass failureClass, string code, bool retrySameModel, bool fallback, bool countsTowardCircuit)
    {
        var failure = new ProviderFailure(failureClass, code, null, null, RequestSent: true);

        var decision = RetryPolicy.Classify(failure);

        decision.RetrySameModel.ShouldBe(retrySameModel, situation);
        decision.Fallback.ShouldBe(fallback, situation);
        CircuitFailureRules.Counts(failure).ShouldBe(countsTowardCircuit, situation);
    }

    [Fact]
    public void Every_adapter_failure_code_has_a_row()
    {
        var codes = typeof(ProviderFailureCodes).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
        var covered = _rows.Select(r => r.Code).ToHashSet();

        codes.Except(covered).ShouldBeEmpty("a new Adapter failure code needs a row in the classification table and in this test");
    }
}
