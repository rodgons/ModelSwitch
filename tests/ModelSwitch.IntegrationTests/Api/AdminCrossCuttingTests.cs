using System.Data.Common;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>Admin behaviour that spans modules or instances: one transaction for the limits write, API Key revocation across hosts.</summary>
public sealed class AdminCrossCuttingTests(AdminFixture admin) : IClassFixture<AdminFixture>
{
    [Fact]
    public async Task A_failing_budget_write_rolls_back_the_limits_row_the_audit_record_and_the_config_version()
    {
        await using var host = admin.StartHost(services =>
        {
            var original = services.Single(d => d.ServiceType == typeof(ITenantBudgets));
            services.Remove(original);
            services.Add(new ServiceDescriptor(typeof(ITenantBudgets), sp => new FailingAfterWrite((ITenantBudgets)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)), original.Lifetime));
        });
        using var owner = AdminFixture.NewBrowser(host);
        await owner.SignInAsync(AdminFixture.OwnerEmail, AdminFixture.OwnerPassword);
        var tenant = (await AdminBrowser.JsonAsync(await owner.SendJsonAsync(HttpMethod.Post, "/admin/tenants", new { name = $"atomic-{Guid.CreateVersion7()}" }))).GetProperty("id").GetGuid();
        var versionBefore = await admin.ScalarAsync("select version from config.config_version");

        var response = await owner.SendJsonAsync(HttpMethod.Put, $"/admin/tenants/{tenant}/limits", new { requestsPerMinute = 9, budget = new { amount = 5, period = "month" } });

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await admin.ScalarAsync($"select count(*) from identity.tenant_limits where tenant_id = '{tenant}'")).ShouldBe("0");
        (await admin.ScalarAsync($"select count(*) from usage.budget where tenant_id = '{tenant}'")).ShouldBe("0");
        (await admin.ScalarAsync($"select count(*) from identity.audit_record where action = 'tenant.limits_changed' and target_id = '{tenant}'")).ShouldBe("0");
        (await admin.ScalarAsync("select version from config.config_version")).ShouldBe(versionBefore);
    }

    [Fact]
    public async Task A_revoked_key_stops_at_once_on_the_revoking_host_and_within_the_cache_bound_on_the_other()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        void UseFakeTime(IServiceCollection services) => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(time));
        await using var hostA = admin.StartHost(UseFakeTime);
        await using var hostB = admin.StartHost(UseFakeTime);
        using var owner = AdminFixture.NewBrowser(hostA);
        await owner.SignInAsync(AdminFixture.OwnerEmail, AdminFixture.OwnerPassword);
        var created = await AdminBrowser.JsonAsync(await owner.SendJsonAsync(HttpMethod.Post, $"/admin/tenants/{admin.TenantA}/api-keys", new { name = "two-hosts" }));
        var keyId = created.GetProperty("id").GetGuid();
        var key = created.GetProperty("key").GetString()!;
        using var clientA = Bearer(hostA, key);
        using var clientB = Bearer(hostB, key);
        var ct = TestContext.Current.CancellationToken;
        (await clientB.GetAsync("/v1/models", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await owner.SendJsonAsync(HttpMethod.Delete, $"/admin/tenants/{admin.TenantA}/api-keys/{keyId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await clientA.GetAsync("/v1/models", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await clientB.GetAsync("/v1/models", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        time.Advance(TimeSpan.FromSeconds(31));
        (await clientB.GetAsync("/v1/models", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static HttpClient Bearer(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ModelSwitch.Api.ApiHost> host, string key)
    {
        var client = host.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    /// <summary>Runs the real Budget write on the caller's transaction, then fails: the whole admin write must roll back.</summary>
    private sealed class FailingAfterWrite(ITenantBudgets inner) : ITenantBudgets
    {
        public Task<decimal?> GetMonthlyLimitAsync(Guid tenantId, CancellationToken cancellationToken) => inner.GetMonthlyLimitAsync(tenantId, cancellationToken);

        public Task<TenantBudgetPeriod> GetCurrentPeriodAsync(Guid tenantId, CancellationToken cancellationToken) => inner.GetCurrentPeriodAsync(tenantId, cancellationToken);

        public async Task SetMonthlyLimitAsync(Guid tenantId, decimal? amount, DbTransaction transaction, CancellationToken cancellationToken)
        {
            await inner.SetMonthlyLimitAsync(tenantId, amount, transaction, cancellationToken);
            throw new InvalidOperationException("budget write failed");
        }
    }
}
