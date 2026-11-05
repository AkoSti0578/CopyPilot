using CopyPilot.Core.Models;
using CopyPilot.Core.Services;
using CopyPilot.Core.SharePoint;
using Xunit;

namespace CopyPilot.Core.Tests;

/// <summary>
/// Created / Modified / Created By / Modified By via ValidateUpdateListItem, against the fake SharePoint REST API
/// (site locale nl-NL, time zone Europe/Amsterdam).
/// </summary>
public class SharePointMetadataTests
{
    private const string Alice = "i:0#.f|membership|alice@contoso.com";
    private const string Bob = "i:0#.f|membership|bob@contoso.com";

    private static LocationSpec Library(string path) => new()
    {
        Kind = LocationKind.SharePoint,
        SiteUrl = "https://contoso.sharepoint.com/sites/Team",
        Library = "Documents",
        ClientId = "00000000-0000-0000-0000-000000000001",
        Path = path,
    };

    private static async Task<(int Code, List<string> Log, FakeTokens Tokens)> Run(FakeGraph graph, LocationSpec src, LocationSpec dst, string mode)
    {
        var tokens = new FakeTokens();
        var plan = TransferPlanner.Plan(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy),
            Source = src,
            Destination = dst,
            Options = new Dictionary<string, string> { ["recursive"] = "true", ["sharepoint-preserve-metadata"] = mode },
        });
        Assert.True(plan.IsValid, string.Join(", ", plan.Errors));
        var runner = new TransferRunner(new AzCopyRunner())
        {
            GraphHandler = graph,
            GraphBaseUrl = FakeGraph.Base,
            GraphTokenProviderFactory = (_, _) => tokens,
        };
        var log = new List<string>();
        var code = await runner.RunAsync(plan, null, new Dictionary<string, string>(), (l, _) => { lock (log) log.Add(l); }, CancellationToken.None);
        return (code, log, tokens);
    }

    private static void Near(DateTime expected, DateTime actual) =>
        Assert.True((expected - actual).Duration() <= TimeSpan.FromSeconds(61), $"expected {expected:u}, got {actual:u}");

    [Fact]
    public async Task Local_to_sharepoint_sets_created_and_modified_in_site_time()
    {
        var graph = new FakeGraph();
        using var local = new TempDir();
        var summer = new DateTime(2025, 6, 15, 12, 34, 0, DateTimeKind.Utc);
        var dstSwitch = new DateTime(2025, 3, 30, 1, 30, 0, DateTimeKind.Utc); // Amsterdam switches to summer time at 01:00 UTC
        var a = local.Write("in/a.txt", "a", summer);
        var b = local.Write("in/dst.txt", "b", dstSwitch);

        var (code, log, tokens) = await Run(graph, new LocationSpec { LocalPath = Path.Combine(local.Path, "in") }, Library("out"), "dates");

        Assert.Equal(0, code);
        Assert.Empty(graph.Problems);
        Assert.DoesNotContain(log, l => l.StartsWith("WARNING"));
        Assert.Contains("https://contoso.sharepoint.com", tokens.Resources);

        var na = graph.Items["out/in/a.txt"];
        Near(summer, na.ItemModified);
        var createdA = File.GetCreationTimeUtc(a) <= summer ? File.GetCreationTimeUtc(a) : summer;
        Near(createdA, na.ItemCreated);
        Assert.Equal(FakeGraph.Uploader, na.Author); // dates only: authors untouched

        Near(dstSwitch, graph.Items["out/in/dst.txt"].ItemModified);

        // Sent in the site's own format and time zone: 12:34 UTC is 14:34 in Amsterdam (summer).
        Assert.True(graph.ValidateBodies.Any(body => System.Text.RegularExpressions.Regex.IsMatch(body, @"15-0?6-2025 14:34")),
            string.Join("\n", graph.ValidateBodies));
        Assert.True(graph.UtcToLocalCalls <= 12, $"{graph.UtcToLocalCalls} time zone calls; offsets should be cached per day");
    }

    [Fact]
    public async Task Sharepoint_to_sharepoint_carries_authors_and_keeps_uploader_for_unknown_users()
    {
        var graph = new FakeGraph();
        graph.KnownUsers.Add(Alice); // bob has left the organisation
        graph.Put("src/report.docx", "report", new DateTime(2024, 2, 3, 10, 15, 0, DateTimeKind.Utc));
        var source = graph.Items["src/report.docx"];
        source.ItemCreated = new DateTime(2024, 1, 2, 9, 0, 0, DateTimeKind.Utc);
        source.ItemModified = new DateTime(2024, 2, 3, 10, 15, 0, DateTimeKind.Utc);
        source.Author = Alice;
        source.Editor = Bob;

        var (code, log, _) = await Run(graph, Library("src/report.docx"), Library("dst/"), "dates-and-authors");

        Assert.Equal(0, code);
        Assert.Empty(graph.Problems);
        var copy = graph.Items["dst/report.docx"];
        Near(source.ItemCreated, copy.ItemCreated);
        Near(source.ItemModified, copy.ItemModified);
        Assert.Equal(Alice, copy.Author);
        Assert.Equal(FakeGraph.Uploader, copy.Editor);
        Assert.Contains(log, l => l.Contains("bob@contoso.com") && l.Contains("could not be set as Modified By"));
    }

    [Fact]
    public async Task Falls_back_to_iso_when_the_site_rejects_its_own_format()
    {
        var graph = new FakeGraph { AcceptIsoOnly = true };
        using var local = new TempDir();
        var modified = new DateTime(2025, 1, 20, 8, 5, 0, DateTimeKind.Utc);
        local.Write("x.txt", "x", modified);

        var (code, log, _) = await Run(graph, new LocationSpec { LocalPath = Path.Combine(local.Path, "x.txt") }, Library("out/"), "dates");

        Assert.Equal(0, code);
        Assert.Contains(log, l => l.Contains("switching to ISO"));
        Near(modified, graph.Items["out/x.txt"].ItemModified);
    }

    [Fact]
    public async Task Detects_day_and_month_swapped_by_the_site_and_corrects_it()
    {
        // The site claims nl-NL but parses like en-US: "3-4-2025" would become 4 March instead of 3 April.
        var graph = new FakeGraph { ParseCultureOverride = "en-US" };
        using var local = new TempDir();
        var modified = new DateTime(2025, 4, 3, 10, 0, 0, DateTimeKind.Utc);
        local.Write("x.txt", "x", modified);

        var (code, log, _) = await Run(graph, new LocationSpec { LocalPath = Path.Combine(local.Path, "x.txt") }, Library("out/"), "dates");

        Assert.Equal(0, code);
        Assert.Contains(log, l => l.Contains("switching to ISO"));
        Near(modified, graph.Items["out/x.txt"].ItemModified);
    }

    [Fact]
    public async Task Missing_sharepoint_permission_warns_but_still_copies()
    {
        var graph = new FakeGraph { RestForbidden = true };
        using var local = new TempDir();
        local.Write("x.txt", "content");

        var (code, log, _) = await Run(graph, new LocationSpec { LocalPath = Path.Combine(local.Path, "x.txt") }, Library("out/"), "dates");

        Assert.Equal(0, code);
        Assert.Equal("content", graph.Text("out/x.txt"));
        Assert.Contains(log, l => l.StartsWith("WARNING: could not set Created/Modified") && l.Contains("AllSites.Write"));
        Assert.Contains(log, l => l.Contains("could not be set on 1 file(s)"));
    }

    [Fact]
    public void Planner_checks_metadata_option()
    {
        var copy = CommandCatalog.Get(CommandCatalog.Copy);
        ExecutionPlan Plan(LocationSpec src, LocationSpec dst, string mode) => TransferPlanner.Plan(new CommandRequest
        {
            Command = copy, Source = src, Destination = dst,
            Options = new Dictionary<string, string> { ["sharepoint-preserve-metadata"] = mode },
        });

        var local = new LocationSpec { LocalPath = "/x" };
        var secret = Library("out");
        secret.SharePointAuth = SharePointAuthMode.ClientSecret;
        secret.TenantId = "contoso.onmicrosoft.com";
        secret.ClientSecret = "s";

        Assert.Contains(Plan(local, secret, "dates").Errors, e => e.Contains("client secret"));
        Assert.Contains(Plan(local, Library("out"), "dates-and-authors").Warnings, w => w.Contains("only Created and Modified"));
        Assert.Contains(Plan(Library("in"), local, "dates").Warnings, w => w.Contains("only applies when the destination is SharePoint"));
        Assert.Contains(Plan(local, Library("out"), "yes please").Errors, e => e.Contains("none, dates or dates-and-authors"));
        Assert.Equal(SharePointMetadataMode.DatesAndAuthors, Plan(Library("in"), Library("out"), "dates-and-authors").SharePointMetadata);
        Assert.True(Plan(Library("in"), Library("out"), "dates-and-authors").IsValid);
    }
}
