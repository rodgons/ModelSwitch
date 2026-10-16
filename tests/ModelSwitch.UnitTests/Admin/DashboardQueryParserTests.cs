using ModelSwitch.Admin.Application;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;
using Shouldly;

namespace ModelSwitch.UnitTests.Admin;

/// <summary>api.md section 3, "Requests and usage": the dashboard's query strings and their validation.</summary>
public sealed class DashboardQueryParserTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_bare_request_list_has_no_time_bound_and_pages_newest_first()
    {
        var query = DashboardQueryParser.Requests(new DashboardQueryString(), _now).Value;

        query.Filter.From.ShouldBeNull();
        query.Filter.To.ShouldBeNull();
        query.Sort.ShouldBe(RequestSort.StartedAtDescending);
        query.Page.Limit.ShouldBe(50);
    }

    [Fact]
    public void Every_filter_is_parsed()
    {
        var tenant = Guid.CreateVersion7();
        var provider = Guid.CreateVersion7();
        var model = Guid.CreateVersion7();
        var query = DashboardQueryParser.Requests(
            new DashboardQueryString
            {
                From = "2026-10-14T00:00:00Z",
                To = "2026-10-15T00:00:00Z",
                TenantId = tenant.ToString(),
                Origin = "evaluation",
                Alias = "balanced",
                ProviderId = provider.ToString(),
                ModelId = model.ToString(),
                Status = "failed",
                ErrorCode = "provider_unavailable",
                HadFallback = "true",
                Sort = "durationMs",
                Order = "asc",
                Limit = "10",
            },
            _now).Value;

        query.Filter.ShouldBe(new RequestFilter(
            null,
            new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero),
            tenant,
            RequestOrigin.Evaluation,
            "balanced",
            provider,
            model,
            "failed",
            "provider_unavailable",
            true));
        query.Sort.ShouldBe(RequestSort.DurationAscending);
        query.Page.Limit.ShouldBe(10);
    }

    [Fact]
    public void Every_invalid_parameter_is_reported_at_once_as_validation_failed()
    {
        var result = DashboardQueryParser.Requests(
            new DashboardQueryString
            {
                From = "yesterday",
                TenantId = "acme",
                Origin = "batch",
                Alias = "Not An Alias",
                Status = "done",
                ErrorCode = "oops",
                HadFallback = "yes",
                Sort = "cost",
                Order = "up",
                Limit = "0",
                Cursor = "%%%",
            },
            _now);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCode.ValidationFailed);
        result.Error.FieldErrors!.Keys.ShouldBe(
            ["from", "origin", "alias", "status", "errorCode", "hadFallback", "tenantId", "sort", "order", "limit", "cursor"],
            ignoreOrder: true);
    }

    [Fact]
    public void From_must_be_before_to_and_the_range_is_bounded()
    {
        DashboardQueryParser.Requests(new DashboardQueryString { From = "2026-10-15T00:00:00Z", To = "2026-10-14T00:00:00Z" }, _now)
            .Error.FieldErrors!.Keys.ShouldBe(["from"]);
        DashboardQueryParser.Overview(new DashboardQueryString { From = "2024-01-01T00:00:00Z" }, _now)
            .Error.FieldErrors!.Keys.ShouldBe(["from"]);
    }

    [Fact]
    public void Reports_default_to_the_last_24_hours()
    {
        var overview = DashboardQueryParser.Overview(new DashboardQueryString(), _now).Value;

        overview.From.ShouldBe(_now.AddHours(-24));
        overview.To.ShouldBe(_now);
    }

    [Fact]
    public void Usage_groups_by_any_combination_and_provider_is_folded_from_provider_models()
    {
        var parsed = DashboardQueryParser.Usage(new DashboardQueryString { GroupBy = ["tenant,provider", "alias"], Interval = "month" }, _now).Value;

        parsed.Query.GroupBy.ShouldBe(UsageGrouping.Tenant | UsageGrouping.ProviderModel | UsageGrouping.Alias);
        parsed.GroupByProvider.ShouldBeTrue();
        parsed.GroupByModel.ShouldBeFalse();
        parsed.Query.Interval.ShouldBe(UsageInterval.Month);
    }

    [Fact]
    public void Usage_rejects_unknown_groupings_and_hourly_reports_over_a_month()
    {
        var result = DashboardQueryParser.Usage(
            new DashboardQueryString { GroupBy = ["region"], Interval = "hour", From = "2026-08-01T00:00:00Z" },
            _now);

        result.Error.FieldErrors!.Keys.ShouldBe(["groupBy", "interval"], ignoreOrder: true);
    }
}
