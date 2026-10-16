using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>
/// frontend.md section 1: the dashboard's TypeScript client is generated from <c>/openapi/v1.json</c>, committed as
/// <c>web/openapi.json</c>. This test fails when the served document drifts from the committed one; run it with
/// <c>UPDATE_OPENAPI=1</c> to rewrite the file, then <c>pnpm generate:api</c> in <c>web/</c>.
/// </summary>
public sealed class OpenApiDocumentTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    [Fact]
    [HappyPath("GET /openapi/{documentName}.json")]
    public async Task The_served_document_matches_the_committed_one_and_describes_the_admin_responses()
    {
        var response = await gateway.Anonymous.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var served = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        // The dashboard's shapes come from here, so the read endpoints must describe their bodies.
        served["paths"]!["/admin/requests"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"].ShouldNotBeNull();
        served["components"]!["schemas"]!["RequestDetailView"].ShouldNotBeNull();

        var text = served.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var path = Path.Combine(RepositoryRoot(), "web", "openapi.json");
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        }

        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n")
            .ShouldBe(text, "web/openapi.json is out of date: run this test with UPDATE_OPENAPI=1, then pnpm generate:api in web/");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ModelSwitch.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("ModelSwitch.slnx was not found above the test binaries.");
    }
}
