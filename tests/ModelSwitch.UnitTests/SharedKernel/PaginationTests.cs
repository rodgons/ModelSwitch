using ModelSwitch.SharedKernel.Pagination;
using Shouldly;

namespace ModelSwitch.UnitTests.SharedKernel;

public sealed class PaginationTests
{
    [Fact]
    public void Defaults_to_fifty_with_no_cursor()
    {
        var page = Pagination.Parse(null, null);

        page.Value.Limit.ShouldBe(50);
        page.Value.After.ShouldBeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("201")]
    [InlineData("-1")]
    [InlineData("many")]
    public void A_limit_outside_1_to_200_is_a_validation_error(string limit)
    {
        var page = Pagination.Parse(limit, null);

        page.IsFailure.ShouldBeTrue();
        page.Error.FieldErrors!.ContainsKey("limit").ShouldBeTrue();
    }

    [Fact]
    public void A_cursor_round_trips_and_garbage_is_a_validation_error()
    {
        Pagination.Parse("5", Pagination.Encode("key-1")).Value.After.ShouldBe("key-1");
        Pagination.Parse(null, "%%%").Error.FieldErrors!.ContainsKey("cursor").ShouldBeTrue();
    }

    [Fact]
    public void The_extra_row_read_means_another_page_exists_and_is_not_returned()
    {
        var more = Pagination.Page(["a", "b", "c"], 2, x => x);
        var last = Pagination.Page(["a", "b"], 2, x => x);

        more.Items.ShouldBe(["a", "b"]);
        Pagination.TryDecode(more.NextCursor!, out var key).ShouldBeTrue();
        key.ShouldBe("b");
        last.NextCursor.ShouldBeNull();
    }
}
