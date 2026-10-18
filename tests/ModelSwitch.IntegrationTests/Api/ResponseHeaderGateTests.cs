using System.Reflection;
using System.Text.RegularExpressions;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>
/// The response-header gate (testing.md section 6): every header in a "Response headers" table of docs/api.md has an integration
/// test tagged <see cref="AssertsHeaderAttribute"/>, so the docs cannot promise a header the gateway never sends.
/// </summary>
public sealed partial class ResponseHeaderGateTests
{
    [Fact]
    public void Every_documented_response_header_is_asserted_by_a_tagged_test()
    {
        var documented = DocumentedResponseHeaders(File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "api.md")));
        var tagged = typeof(AssertsHeaderAttribute).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(method => method.GetCustomAttributes<AssertsHeaderAttribute>())
            .Select(attribute => attribute.Header)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        documented.ShouldNotBeEmpty("docs/api.md has no Response headers table");
        documented.Except(tagged, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty("documented response headers without an [AssertsHeader] integration test");
        tagged.Except(documented, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty("[AssertsHeader] tags that name no documented response header");
    }

    /// <summary>
    /// The header names in the first column of every table under a heading containing "Response headers". A cell may list several
    /// names; one that starts with <c>-</c> (<c>`X-RateLimit-Limit` / `-Remaining`</c>) shares the first name's prefix.
    /// </summary>
    private static HashSet<string> DocumentedResponseHeaders(string markdown)
    {
        var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith('#'))
            {
                inSection = line.Contains("Response headers", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection || !line.StartsWith('|') || line.StartsWith("|---", StringComparison.Ordinal))
            {
                continue;
            }

            var firstCell = line.Split('|', StringSplitOptions.TrimEntries)[1];
            string? prefix = null;
            foreach (Match match in CodeSpan().Matches(firstCell))
            {
                var name = match.Groups[1].Value;
                if (name.StartsWith('-') && prefix is not null)
                {
                    headers.Add(prefix + name);
                }
                else
                {
                    headers.Add(name);
                    prefix = name.Contains('-', StringComparison.Ordinal) ? name[..name.LastIndexOf('-')] : name;
                }
            }
        }

        return headers;
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

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex CodeSpan();
}
