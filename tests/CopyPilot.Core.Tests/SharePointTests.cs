using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CopyPilot.Core.Models;
using CopyPilot.Core.Services;
using CopyPilot.Core.SharePoint;
using CopyPilot.Core.Sftp;
using Xunit;

namespace CopyPilot.Core.Tests;

/// <summary>
/// An in-memory stand-in for the parts of Microsoft Graph that CopyPilot uses. It enforces what real Graph
/// requires: a bearer token on graph calls, none on pre-authenticated upload/download URLs, upload chunks in
/// multiples of 320 KiB with correct Content-Range, and paged child listings.
/// </summary>
public sealed class FakeGraph : HttpMessageHandler
{
    public const string Base = "https://graph.fake/v1.0";
    public const string Token = "test-token";

    public sealed class Node
    {
        public bool IsFolder;
        public byte[] Data = [];
        public DateTime Modified = DateTime.UtcNow;

        // SharePoint list item fields (what the library shows).
        public int ListItemId;
        public DateTime ItemCreated = DateTime.UtcNow;
        public DateTime ItemModified = DateTime.UtcNow;
        public string Author = Uploader;
        public string Editor = Uploader;
    }

    public const string SpToken = "sp-token";
    public const string ListId = "11111111-2222-3333-4444-555555555555";
    public const string Uploader = "i:0#.f|membership|copypilot@contoso.com";

    /// <summary>The site's regional settings, and how the fake parses dates (normally the same culture).</summary>
    public int SiteLcid { get; set; } = 1043;
    public string? ParseCultureOverride { get; set; }
    public bool AcceptIsoOnly { get; set; }
    public bool RestForbidden { get; set; }
    public HashSet<string> KnownUsers { get; } = new(StringComparer.OrdinalIgnoreCase) { Uploader };
    public int UtcToLocalCalls { get; private set; }
    public List<string> ValidateBodies { get; } = [];
    private int _nextItemId;
    private static readonly TimeZoneInfo SiteZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    /// <summary>Library content keyed by path ("" = root).</summary>
    public Dictionary<string, Node> Items { get; } = new(StringComparer.OrdinalIgnoreCase) { [""] = new Node { IsFolder = true } };

    public int ThrottleNext { get; set; }
    public int Throttled { get; private set; }
    public int PageSize { get; set; } = 2;
    public List<string> Requests { get; } = [];
    public List<long> ChunkSizes { get; } = [];
    public List<string> Problems { get; } = [];

    private readonly Dictionary<string, (string Path, long Length, DateTime? Modified, MemoryStream Data)> _sessions = new();

    public void Put(string path, string content, DateTime? modified = null)
    {
        var parts = path.Split('/');
        for (var i = 1; i < parts.Length; i++) Items.TryAdd(string.Join('/', parts.Take(i)), new Node { IsFolder = true });
        Items[path] = new Node { Data = Encoding.UTF8.GetBytes(content), Modified = modified ?? DateTime.UtcNow };
    }

    public string Text(string path) => Encoding.UTF8.GetString(Items[path].Data);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        var auth = request.Headers.Authorization?.Parameter;
        Requests.Add($"{request.Method} {url}");
        var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(ct);

        if (ThrottleNext > 0)
        {
            ThrottleNext--;
            Throttled++;
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return r;
        }

        if (url.Host == "upload.fake")
        {
            if (auth != null) Problems.Add("token sent to upload URL");
            return Upload(url.AbsolutePath.Trim('/'), request, body);
        }

        if (url.Host == "download.fake")
        {
            if (auth != null) Problems.Add("token sent to download URL");
            var p = Uri.UnescapeDataString(url.AbsolutePath.Trim('/'));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Items[p].Data) };
        }

        if (url.Host == "contoso.sharepoint.com")
        {
            if (auth != SpToken) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (!request.Headers.Accept.ToString().Contains("odata=nometadata")) Problems.Add("REST call without odata=nometadata");
            return SharePointRest(request, Uri.UnescapeDataString(url.PathAndQuery), body);
        }

        if (auth != Token) return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var path = Uri.UnescapeDataString(url.AbsolutePath);
        if (path == "/v1.0/sites/contoso.sharepoint.com:/sites/Team")
            return Json(new JsonObject { ["id"] = "site1", ["displayName"] = "Team" });
        if (path == "/v1.0/sites/site1/drives")
            return Json(new JsonObject
            {
                ["value"] = new JsonArray(
                    new JsonObject { ["id"] = "other", ["name"] = "Archive", ["webUrl"] = "https://contoso.sharepoint.com/sites/Team/Archive" },
                    new JsonObject { ["id"] = "drv1", ["name"] = "Documents", ["webUrl"] = "https://contoso.sharepoint.com/sites/Team/Shared%20Documents" }),
            });

        const string root = "/v1.0/drives/drv1/root";
        if (!path.StartsWith(root, StringComparison.Ordinal)) return Error(HttpStatusCode.NotFound, "itemNotFound");

        // root, root/children, root:/a/b, root:/a/b:/children, …
        var rest = path[root.Length..];
        string itemPath, action = "";
        if (rest.StartsWith(":/", StringComparison.Ordinal))
        {
            rest = rest[2..];
            var colon = rest.IndexOf(":/", StringComparison.Ordinal);
            if (colon >= 0)
            {
                action = rest[(colon + 1)..];
                rest = rest[..colon];
            }

            itemPath = rest;
        }
        else
        {
            itemPath = "";
            action = rest;
        }

        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        switch (request.Method.Method, action)
        {
            case ("GET", ""):
                return Items.TryGetValue(itemPath, out var node) ? Json(ItemJson(itemPath, node)) : Error(HttpStatusCode.NotFound, "itemNotFound");
            case ("GET", "/children"):
            {
                var children = Items.Keys.Where(k => k.Length > 0 && Parent(k).Equals(itemPath, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k, StringComparer.Ordinal).ToList();
                var skip = int.TryParse(query["skip"], out var s) ? s : 0;
                var page = new JsonArray(children.Skip(skip).Take(PageSize).Select(k => (JsonNode)ItemJson(k, Items[k])).ToArray());
                var result = new JsonObject { ["value"] = page };
                if (skip + PageSize < children.Count)
                    result["@odata.nextLink"] = $"{Base}/drives/drv1/root{(itemPath.Length > 0 ? ":/" + Uri.EscapeDataString(itemPath).Replace("%2F", "/") + ":" : "")}/children?skip={skip + PageSize}";
                return Json(result);
            }
            case ("POST", "/children"):
            {
                var j = JsonNode.Parse(body)!;
                var name = j["name"]!.GetValue<string>();
                var p = itemPath.Length == 0 ? name : itemPath + "/" + name;
                if (Items.ContainsKey(p)) return Error(HttpStatusCode.Conflict, "nameAlreadyExists");
                Items[p] = new Node { IsFolder = true };
                return Json(ItemJson(p, Items[p]), HttpStatusCode.Created);
            }
            case ("POST", "/createUploadSession"):
            {
                var j = JsonNode.Parse(body)!;
                var modified = j["item"]?["fileSystemInfo"]?["lastModifiedDateTime"]?.GetValue<string>();
                var id = Guid.NewGuid().ToString("N");
                _sessions[id] = (itemPath, -1, modified == null ? null : DateTime.Parse(modified).ToUniversalTime(), new MemoryStream());
                return Json(new JsonObject { ["uploadUrl"] = $"https://upload.fake/{id}" });
            }
            case ("PUT", "/content"):
                Items[itemPath] = new Node { Data = body };
                return Json(ItemJson(itemPath, Items[itemPath]), HttpStatusCode.Created);
            case ("PATCH", ""):
            {
                var modified = JsonNode.Parse(body)?["fileSystemInfo"]?["lastModifiedDateTime"]?.GetValue<string>();
                if (modified != null) Items[itemPath].Modified = DateTime.Parse(modified).ToUniversalTime();
                return Json(ItemJson(itemPath, Items[itemPath]));
            }
            case ("DELETE", ""):
                return Items.Remove(itemPath) ? new HttpResponseMessage(HttpStatusCode.NoContent) : Error(HttpStatusCode.NotFound, "itemNotFound");
        }

        return Error(HttpStatusCode.BadRequest, "unsupportedInFake");
    }

    private HttpResponseMessage Upload(string id, HttpRequestMessage request, byte[] body)
    {
        if (request.Method == HttpMethod.Delete)
        {
            _sessions.Remove(id);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        var s = _sessions[id];
        var range = request.Content!.Headers.ContentRange!;
        if (range.From != s.Data.Length) Problems.Add($"chunk starts at {range.From}, expected {s.Data.Length}");
        if (range.To - range.From + 1 != body.Length) Problems.Add("Content-Range does not match the body");
        var isLast = range.To + 1 == range.Length;
        if (!isLast && body.Length % SharePointFileTree.ChunkUnit != 0) Problems.Add($"chunk of {body.Length} is not a multiple of 320 KiB");
        ChunkSizes.Add(body.Length);
        s.Data.Write(body);
        if (!isLast) return new HttpResponseMessage(HttpStatusCode.Accepted);

        Put(s.Path, "");
        Items[s.Path] = new Node { Data = s.Data.ToArray(), Modified = s.Modified ?? DateTime.UtcNow, ListItemId = ++_nextItemId };
        _sessions.Remove(id);
        return Json(ItemJson(s.Path, Items[s.Path]), HttpStatusCode.Created);
    }

    private Node? ByItemId(int id) => Items.Values.FirstOrDefault(n => n.ListItemId == id);

    private static string Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private HttpResponseMessage SharePointRest(HttpRequestMessage request, string path, byte[] body)
    {
        if (RestForbidden) return Error(HttpStatusCode.Forbidden, "accessDenied");
        if (path == "/sites/Team/_api/web/RegionalSettings?$select=LocaleId") return Json(new JsonObject { ["LocaleId"] = SiteLcid });

        var tz = System.Text.RegularExpressions.Regex.Match(path, @"^/sites/Team/_api/web/RegionalSettings/TimeZone/UTCToLocalTime\(@date\)\?@date='([^']+)'$");
        if (tz.Success)
        {
            UtcToLocalCalls++;
            var utc = DateTime.Parse(tz.Groups[1].Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            return Json(new JsonObject { ["value"] = TimeZoneInfo.ConvertTimeFromUtc(utc, SiteZone).ToString("yyyy-MM-dd'T'HH:mm:ss") });
        }

        var item = System.Text.RegularExpressions.Regex.Match(path,
            @"^/sites/Team/_api/web/lists\(guid'" + ListId + @"'\)/items\((\d+)\)(/ValidateUpdateListItem|\?\$select=Created,Modified,Author/Name,Editor/Name&\$expand=Author,Editor)$");
        if (!item.Success) return Error(HttpStatusCode.NotFound, "notFoundInFake " + path);
        var node = ByItemId(int.Parse(item.Groups[1].Value));
        if (node == null) return Error(HttpStatusCode.NotFound, "itemNotFound");

        if (request.Method == HttpMethod.Get)
            return Json(new JsonObject
            {
                ["Created"] = Utc(node.ItemCreated), ["Modified"] = Utc(node.ItemModified),
                ["Author"] = new JsonObject { ["Name"] = node.Author }, ["Editor"] = new JsonObject { ["Name"] = node.Editor },
            });

        var text = Encoding.UTF8.GetString(body);
        ValidateBodies.Add(text);
        var json = JsonNode.Parse(text)!;
        if (json["bNewDocumentUpdate"]?.GetValue<bool>() != true) Problems.Add("bNewDocumentUpdate must be true (no new version)");

        // Like SharePoint: validate every field; only save when all of them are valid.
        var results = new JsonArray();
        var updates = new List<Action>();
        foreach (var fv in json["formValues"]!.AsArray())
        {
            var field = fv!["FieldName"]!.GetValue<string>();
            var value = fv["FieldValue"]!.GetValue<string>();
            string? error = null;
            switch (field)
            {
                case "Created" or "Modified":
                    if (ParseSiteDate(value) is { } local)
                    {
                        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), SiteZone);
                        updates.Add(field == "Created" ? () => node.ItemCreated = utc : () => node.ItemModified = utc);
                    }
                    else
                    {
                        error = "Invalid date/time value";
                    }

                    break;
                case "Author" or "Editor":
                    var m = System.Text.RegularExpressions.Regex.Match(value, @"^\[\{'Key':'(i:0#\.f\|membership\|[^']+)'\}\]$");
                    if (!m.Success) error = "Invalid person value";
                    else if (!KnownUsers.Contains(m.Groups[1].Value)) error = "The user does not exist or is not unique.";
                    else updates.Add(field == "Author" ? () => node.Author = m.Groups[1].Value : () => node.Editor = m.Groups[1].Value);
                    break;
                default:
                    error = "Unknown field";
                    break;
            }

            results.Add(new JsonObject
            {
                ["ErrorMessage"] = error, ["FieldName"] = field, ["FieldValue"] = value, ["HasException"] = error != null, ["ItemId"] = node.ListItemId,
            });
        }

        if (results.All(r => r!["HasException"]!.GetValue<bool>() == false))
            foreach (var u in updates) u();
        return Json(new JsonObject { ["value"] = results });
    }

    private DateTime? ParseSiteDate(string value)
    {
        if (DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var iso))
            return iso;
        if (AcceptIsoOnly) return null;
        var culture = System.Globalization.CultureInfo.GetCultureInfo(ParseCultureOverride ?? System.Globalization.CultureInfo.GetCultureInfo(SiteLcid).Name);
        return DateTime.TryParse(value, culture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }

    private static string Parent(string p)
    {
        var i = p.LastIndexOf('/');
        return i < 0 ? "" : p[..i];
    }

    private JsonObject ItemJson(string path, Node n)
    {
        var o = new JsonObject
        {
            ["id"] = path,
            ["name"] = path.Length == 0 ? "root" : path.Split('/')[^1],
            ["fileSystemInfo"] = new JsonObject { ["lastModifiedDateTime"] = n.Modified.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") },
        };
        if (n.ListItemId == 0) n.ListItemId = ++_nextItemId;
        o["createdDateTime"] = Utc(n.ItemCreated);
        o["sharepointIds"] = new JsonObject
        {
            ["siteUrl"] = "https://contoso.sharepoint.com/sites/Team", ["listId"] = ListId, ["listItemId"] = n.ListItemId.ToString(),
        };
        if (n.IsFolder)
        {
            o["folder"] = new JsonObject { ["childCount"] = 0 };
        }
        else
        {
            o["file"] = new JsonObject();
            o["size"] = n.Data.Length;
            o["@microsoft.graph.downloadUrl"] = "https://download.fake/" + Uri.EscapeDataString(path);
        }

        return o;
    }

    private static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string code) =>
        Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = code } }, status);
}

public sealed class FakeTokens : IGraphTokenProvider
{
    public List<string> Resources { get; } = [];

    public Task<string> GetAccessTokenAsync(string resource, bool forceRefresh, CancellationToken ct)
    {
        lock (Resources) Resources.Add(resource);
        return Task.FromResult(resource == "https://graph.microsoft.com" ? FakeGraph.Token : FakeGraph.SpToken);
    }
}

public class SharePointTests
{
    private static LocationSpec Library(string path, string library = "Documents") => new()
    {
        Kind = LocationKind.SharePoint,
        SiteUrl = "https://contoso.sharepoint.com/sites/Team",
        Library = library,
        ClientId = "00000000-0000-0000-0000-000000000001",
        Path = path,
    };

    private static TransferRunner Runner(FakeGraph graph) => new(new AzCopyRunner())
    {
        GraphHandler = graph,
        GraphBaseUrl = FakeGraph.Base,
        GraphTokenProviderFactory = (_, _) => new FakeTokens(),
        SharePointChunkSize = SharePointFileTree.ChunkUnit,
    };

    private static async Task<int> Run(FakeGraph graph, CommandRequest request, List<string> log)
    {
        var plan = TransferPlanner.Plan(request);
        Assert.True(plan.IsValid, string.Join(", ", plan.Errors));
        return await Runner(graph).RunAsync(plan, null, new Dictionary<string, string>(), (l, _) => log.Add(l), CancellationToken.None);
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/Team", "contoso.sharepoint.com", "sites/Team", "")]
    [InlineData("https://contoso.sharepoint.com/sites/Team/Shared%20Documents/Forms/AllItems.aspx", "contoso.sharepoint.com", "sites/Team", "Shared Documents")]
    [InlineData("https://contoso.sharepoint.com/", "contoso.sharepoint.com", "", "")]
    [InlineData("https://contoso-my.sharepoint.com/personal/arnold_contoso_com/Documents", "contoso-my.sharepoint.com", "personal/arnold_contoso_com", "Documents")]
    public void Site_urls_are_parsed(string url, string host, string site, string library)
    {
        Assert.Equal((host, site, library), SharePointFileTree.ParseSiteUrl(url));
    }

    [Theory]
    [InlineData("ok/report.docx", null)]
    [InlineData("a/what?.txt", "character")]
    [InlineData("CON", "reserved")]
    [InlineData("x/~$lock.docx", "~$")]
    [InlineData("_vti_cnf/x", "_vti_")]
    [InlineData("trailing /x", "space")]
    public void Names_are_checked(string path, string? problem)
    {
        var result = SharePointNames.Problem(path);
        if (problem == null) Assert.Null(result);
        else Assert.Contains(problem, result);
        Assert.NotNull(SharePointNames.Problem(new string('a', 390), prefixLength: 20));
    }

    [Fact]
    public void Planner_routes_sharepoint_and_validates()
    {
        var blob = new LocationSpec { Kind = LocationKind.AzureBlob, Account = "a", Container = "c" };
        var copy = CommandCatalog.Get(CommandCatalog.Copy);
        Assert.Equal(SftpRoute.Direct, TransferPlanner.Plan(new CommandRequest { Command = copy, Source = new LocationSpec { LocalPath = "/x" }, Destination = Library("") }).Route);
        Assert.Equal(SftpRoute.SftpToCloud, TransferPlanner.Plan(new CommandRequest { Command = copy, Source = Library("in"), Destination = blob }).Route);
        Assert.Equal(SftpRoute.CloudToSftp, TransferPlanner.Plan(new CommandRequest { Command = copy, Source = blob, Destination = Library("out") }).Route);

        var bad = TransferPlanner.Plan(new CommandRequest
        {
            Command = copy, Source = new LocationSpec { LocalPath = "/x" },
            Destination = new LocationSpec { Kind = LocationKind.SharePoint, SiteUrl = "nope", SharePointAuth = SharePointAuthMode.ClientSecret },
        });
        Assert.Contains(bad.Errors, e => e.Contains("site URL"));
        Assert.Contains(bad.Errors, e => e.Contains("client) id"));
        Assert.Contains(bad.Errors, e => e.Contains("tenant"));
        Assert.Contains(bad.Errors, e => e.Contains("client secret"));

        var remove = TransferPlanner.Plan(new CommandRequest { Command = CommandCatalog.Get(CommandCatalog.Remove), Target = Library("x") });
        Assert.Contains(remove.Errors, e => e.Contains("SharePoint"));
    }

    [Fact]
    public void Profile_strips_sharepoint_secrets()
    {
        var spec = Library("x");
        spec.ClientSecret = "very-secret";
        spec.CertificatePassword = "cert-pw";
        var json = new Profile { Destination = spec }.ToJson(includeSecrets: false);
        Assert.DoesNotContain("very-secret", json);
        Assert.DoesNotContain("cert-pw", json);
        Assert.Contains("contoso.sharepoint.com", json);
    }

    [Fact]
    public async Task Upload_download_sync_and_throttling()
    {
        var graph = new FakeGraph { ThrottleNext = 2 };
        using var local = new TempDir();
        var old = DateTime.UtcNow.AddDays(-3);
        local.Write("data/a.txt", "hello", old);
        local.Write("data/sub/big.bin", new string('x', 1_000_000), old);
        local.Write("data/empty.txt", "", old);
        var copy = CommandCatalog.Get(CommandCatalog.Copy);
        var log = new List<string>();

        // Local folder → SharePoint (library chosen by its URL name), as a sub-folder.
        Assert.Equal(0, await Run(graph, new CommandRequest
        {
            Command = copy,
            Source = new LocationSpec { LocalPath = Path.Combine(local.Path, "data") },
            Destination = Library("Projects/2026", "Shared Documents"),
            Options = new Dictionary<string, string> { ["recursive"] = "true" },
        }, log));
        Assert.Empty(graph.Problems);
        Assert.Equal(2, graph.Throttled);
        Assert.Contains(log, l => l.Contains("throttling"));
        Assert.Equal("hello", graph.Text("Projects/2026/data/a.txt"));
        Assert.Equal(1_000_000, graph.Items["Projects/2026/data/sub/big.bin"].Data.Length);
        Assert.Empty(graph.Items["Projects/2026/data/empty.txt"].Data);
        Assert.Equal(4, graph.ChunkSizes.Count(c => c > 100)); // 1 MB in 320 KiB chunks: 3 full + 1 partial
        Assert.Equal(old.ToString("s"), graph.Items["Projects/2026/data/a.txt"].Modified.ToString("s"));
        Assert.Contains("Final Job Status: Completed", log);

        // SharePoint → local (paged listing with page size 2), times preserved.
        using var back = new TempDir();
        log.Clear();
        Assert.Equal(0, await Run(graph, new CommandRequest
        {
            Command = copy, Source = Library("Projects/2026/data"), Destination = new LocationSpec { LocalPath = back.Path },
            Options = new Dictionary<string, string> { ["recursive"] = "true" },
        }, log));
        Assert.Equal(["data/a.txt", "data/empty.txt", "data/sub/big.bin"], back.Files());
        Assert.Equal(old.ToString("s"), File.GetLastWriteTimeUtc(Path.Combine(back.Path, "data", "a.txt")).ToString("s"));
        Assert.Contains(graph.Requests, r => r.Contains("skip=2")); // followed @odata.nextLink

        // Sync: SharePoint changed the size of an Office-like file, times equal → still skipped (times only).
        graph.Items["Projects/2026/data/a.txt"].Data = Encoding.UTF8.GetBytes("hello + metadata");
        log.Clear();
        Assert.Equal(0, await Run(graph, new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Sync),
            Source = new LocationSpec { LocalPath = Path.Combine(local.Path, "data") },
            Destination = Library("Projects/2026/data"),
            Options = new Dictionary<string, string> { ["delete-destination"] = "true" },
        }, log));
        Assert.Contains(log, l => l.StartsWith("100.0 %, 0 Done, 0 Failed, 0 Pending, 3 Skipped"));

        // Invalid names fail clearly but do not stop the job.
        local.Write("bad/ok.txt", "ok");
        local.Write("bad/~$draft.docx", "x"); // valid on Windows, rejected by SharePoint
        log.Clear();
        Assert.Equal(1, await Run(graph, new CommandRequest
        {
            Command = copy, Source = new LocationSpec { LocalPath = Path.Combine(local.Path, "bad") }, Destination = Library("Projects"),
            Options = new Dictionary<string, string> { ["recursive"] = "true" },
        }, log));
        Assert.Contains(log, l => l.StartsWith("ERROR:") && l.Contains("Office lock file"));
        Assert.Equal("ok", graph.Text("Projects/bad/ok.txt"));
        Assert.Contains("Final Job Status: CompletedWithErrors", log);
    }

    [Fact]
    public async Task Unknown_library_lists_the_available_ones()
    {
        var log = new List<string>();
        var ex = await Assert.ThrowsAsync<IOException>(() => Run(new FakeGraph(), new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy),
            Source = Library("x", "Nope"),
            Destination = new LocationSpec { LocalPath = "/tmp" },
        }, log));
        Assert.Contains("Archive, Documents", ex.Message);
    }

    [Fact]
    public async Task Sharepoint_to_sftp_like_tree_streams_without_local_copy()
    {
        // SharePoint → SharePoint goes through OpenReadAsync/WriteFromAsync (the path SFTP ↔ SharePoint uses too).
        var graph = new FakeGraph();
        graph.Put("in/one.txt", "1");
        graph.Put("in/two.txt", "22");
        var log = new List<string>();
        Assert.Equal(0, await Run(graph, new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy), Source = Library("in/*"), Destination = Library("copy"),
        }, log));
        Assert.Equal("22", graph.Text("copy/two.txt"));
        Assert.Empty(graph.Problems);
    }
}
