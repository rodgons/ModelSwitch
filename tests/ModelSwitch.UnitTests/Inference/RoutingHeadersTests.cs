using ModelSwitch.Inference.Application;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

/// <summary>The ModelSwitch request headers (docs/api.md section 1, ADR-0009).</summary>
public sealed class RoutingHeadersTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("extraction")]
    [InlineData("a_b-9")]
    public void A_valid_or_absent_task_is_accepted(string? value)
        => RoutingHeaders.ValidateTask(value).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("a/b")]
    public void An_invalid_task_is_a_400_naming_the_header(string value)
    {
        var result = RoutingHeaders.ValidateTask(value);

        result.Error.Code.ShouldBe(ErrorCode.InvalidRequest);
        result.Error.FieldErrors!.ShouldContainKey("X-ModelSwitch-Task");
    }

    [Fact]
    public void A_task_longer_than_64_characters_is_rejected()
        => RoutingHeaders.ValidateTask(new string('a', 65)).IsFailure.ShouldBeTrue();

    [Fact]
    public void A_positive_timeout_is_milliseconds()
        => RoutingHeaders.ValidateTimeout("2500").Value.ShouldBe(TimeSpan.FromMilliseconds(2500));

    [Fact]
    public void An_absent_timeout_is_null()
        => RoutingHeaders.ValidateTimeout(null).Value.ShouldBeNull();

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("soon")]
    [InlineData("99999999999")]
    public void An_invalid_timeout_is_a_400_naming_the_header(string value)
    {
        var result = RoutingHeaders.ValidateTimeout(value);

        result.Error.Code.ShouldBe(ErrorCode.InvalidRequest);
        result.Error.FieldErrors!.ShouldContainKey("X-ModelSwitch-Timeout-Ms");
    }
}
