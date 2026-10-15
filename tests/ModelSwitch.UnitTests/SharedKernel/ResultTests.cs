using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.SharedKernel;

public sealed class ResultTests
{
    [Fact]
    public void Success_has_value_and_no_error()
    {
        Result<int> result = 5;
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(5);
        Should.Throw<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void Failure_has_error_and_no_value()
    {
        Result<int> result = new Error(ErrorCode.NotFound, "Tenant not found.");
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCode.NotFound);
        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Non_generic_result_default_is_success()
    {
        Result.Success().IsSuccess.ShouldBeTrue();
        Result.Failure(new Error(ErrorCode.Conflict, "Stale version.")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Map_transforms_success_and_propagates_failure()
    {
        Result<int> ok = 2;
        ok.Map(x => x * 2).Value.ShouldBe(4);
        Result<int> bad = new Error(ErrorCode.Forbidden, "No.");
        bad.Map(x => x * 2).Error.Code.ShouldBe(ErrorCode.Forbidden);
    }

    [Fact]
    public void Validation_error_carries_field_errors()
    {
        var error = Error.Validation("Invalid.", new Dictionary<string, string[]> { ["messages[3].content"] = ["Required."] });
        error.Code.ShouldBe(ErrorCode.ValidationFailed);
        error.FieldErrors!["messages[3].content"].ShouldBe(["Required."]);
    }
}
