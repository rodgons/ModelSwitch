using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.SharedKernel;

public sealed class ErrorCodeTests
{
    // Mirrors the table in docs/api.md section 2 (the closed set).
    public static readonly (string Code, int? Status, string Type)[] Documented =
    [
        ("invalid_request", 400, "invalid_request_error"),
        ("unsupported_parameter", 400, "invalid_request_error"),
        ("context_length_exceeded", 400, "invalid_request_error"),
        ("invalid_api_key", 401, "authentication_error"),
        ("budget_exceeded", 402, "budget_error"),
        ("request_cost_limit_exceeded", 402, "budget_error"),
        ("insufficient_scope", 403, "permission_error"),
        ("tenant_suspended", 403, "permission_error"),
        ("model_not_allowed", 403, "permission_error"),
        ("model_not_found", 404, "invalid_request_error"),
        ("payload_too_large", 413, "invalid_request_error"),
        ("unsupported_media_type", 415, "invalid_request_error"),
        ("no_eligible_model", 422, "routing_error"),
        ("rate_limit_exceeded", 429, "rate_limit_error"),
        ("concurrency_limit_exceeded", 429, "rate_limit_error"),
        ("provider_error", 502, "provider_error"),
        ("provider_unavailable", 503, "provider_error"),
        ("limits_unavailable", 503, "api_error"),
        ("deadline_exceeded", 504, "timeout_error"),
        ("stream_interrupted", null, "provider_error"),
        ("run_cost_cap_reached", null, "budget_error"),
        ("internal_error", 500, "api_error"),
        ("validation_failed", 400, "invalid_request_error"),
        ("forbidden", 403, "permission_error"),
        ("not_found", 404, "invalid_request_error"),
        ("conflict", 409, "invalid_request_error"),
    ];

    public static TheoryData<string, int?, string> Cases()
    {
        var data = new TheoryData<string, int?, string>();
        foreach (var (code, status, type) in Documented)
        {
            data.Add(code, status, type);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_documented_code_exists_with_its_status_and_type(string code, int? status, string type)
    {
        ErrorCode.TryParse(code, out var found).ShouldBeTrue();
        found.Value.ShouldBe(code);
        found.HttpStatus.ShouldBe(status);
        found.Type.ShouldBe(type);
    }

    [Fact]
    public void The_set_is_closed_and_matches_the_documented_table()
    {
        ErrorCode.All.Select(c => c.Value).ShouldBe(Documented.Select(d => d.Code), ignoreOrder: true);
        ErrorCode.All.Select(c => c.Value).Distinct().Count().ShouldBe(ErrorCode.All.Count);
    }

    [Fact]
    public void Api_doc_table_has_no_code_missing_from_the_set()
    {
        var doc = File.ReadAllText(FindRepoFile("docs/api.md"));
        var section = doc[doc.IndexOf("## 2. Error codes", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("## 3.", StringComparison.Ordinal)];
        var codes = section.Split('\n')
            .Where(l => l.StartsWith("| `", StringComparison.Ordinal))
            .Select(l => l.Split('|')[1].Trim().Trim('`'))
            .ToList();

        codes.ShouldNotBeEmpty();
        codes.ShouldBe(ErrorCode.All.Select(c => c.Value), ignoreOrder: true);
    }

    [Fact]
    public void Unknown_code_does_not_parse()
    {
        ErrorCode.TryParse("nope", out _).ShouldBeFalse();
        ErrorCode.TryParse(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void Validation_errors_are_always_400()
    {
        foreach (var code in new[] { ErrorCode.InvalidRequest, ErrorCode.UnsupportedParameter, ErrorCode.ContextLengthExceeded, ErrorCode.ValidationFailed })
        {
            code.HttpStatus.ShouldBe(400);
        }
    }

    [Fact]
    public void Budget_exceeded_is_402_not_429()
    {
        ErrorCode.BudgetExceeded.HttpStatus.ShouldBe(402);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }

        return dir is null ? throw new FileNotFoundException(relative) : Path.Combine(dir.FullName, relative);
    }
}
