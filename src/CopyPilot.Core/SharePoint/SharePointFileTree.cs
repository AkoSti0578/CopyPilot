using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopyPilot.Core.Models;
using CopyPilot.Core.Sftp;

namespace CopyPilot.Core.SharePoint;

/// <summary>A Graph API error with a readable message.</summary>
public sealed class GraphException(HttpStatusCode status, string code, string message)
    : IOException($"SharePoint returned {(int)status} {code}: {message}")
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// A SharePoint Online / OneDrive document library as an <see cref="IFileTree"/>, using Microsoft Graph.
/// Paths are '/'-separated and relative to the library root ("" = root).
/// Uploads use upload sessions (resumable, in chunks) and store the source's last-modified time;
/// throttling (429/503) is retried as instructed by Retry-After.
/// </summary>
public sealed class SharePointFileTree : IFileTree
{
    public const string DefaultGraphBase = "https://graph.microsoft.com/v1.0";

    /// <summary>Upload chunks must be a multiple of 320 KiB.</summary>
    public const int ChunkUnit = 320 * 1024;

    private const int MaxAttempts = 8;
    private const string ItemSelect = "id,name,size,file,folder,fileSystemInfo,lastModifiedDateTime";

    private readonly HttpClient _graph;
    private readonly HttpClient _plain;
    private readonly IGraphTokenProvider _tokens;
    private readonly Action<string> _log;
    private readonly string _graphBase;
    private readonly string _drive;
    private readonly int _prefixLength;
    private readonly HashSet<string> _knownDirectories = new(StringComparer.OrdinalIgnoreCase);

    private SharePointFileTree(HttpClient graph, HttpClient plain, IGraphTokenProvider tokens, Action<string> log,
        string graphBase, string driveId, string displayName, int prefixLength)
    {
        _graph = graph;
        _plain = plain;
        _tokens = tokens;
        _log = log;
        _graphBase = graphBase.TrimEnd('/');
        _drive = $"{_graphBase}/drives/{driveId}";
        DisplayName = displayName;
        _prefixLength = prefixLength;
    }

    public string DisplayName { get; }
    public bool IsLocal => false;

    /// <summary>SharePoint may add metadata to Office files, changing their size; compare times only.</summary>
    public bool HasExactSizes => false;

    public bool SetsLastWriteTimeOnWrite => true;

    /// <summary>Upload chunk size (multiple of 320 KiB). Default 10 MiB.</summary>
    public int ChunkSize { get; set; } = 32 * ChunkUnit;

    /// <summary>Connects to the site and finds the document library.</summary>
    public static async Task<SharePointFileTree> ConnectAsync(LocationSpec spec, IGraphTokenProvider tokens, Action<string> log,
        CancellationToken ct, HttpMessageHandler? handler = null, string graphBase = DefaultGraphBase)
    {
        var graph = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        var plain = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        graph.Timeout = plain.Timeout = TimeSpan.FromMinutes(30);
        try
        {
            var (host, sitePath, libraryFromUrl) = ParseSiteUrl(spec.SiteUrl);
            var library = string.IsNullOrWhiteSpace(spec.Library) ? libraryFromUrl : spec.Library.Trim();

            // A temporary tree to reuse the request/retry logic before the drive is known.
            var probe = new SharePointFileTree(graph, plain, tokens, log, graphBase, "-", "", 0);
            var siteUrl = sitePath.Length == 0
                ? $"{probe._graphBase}/sites/{host}?$select=id,displayName,webUrl"
                : $"{probe._graphBase}/sites/{host}:/{EscapePath(sitePath)}?$select=id,displayName,webUrl";
            var site = await probe.GetJsonAsync(siteUrl, ct).ConfigureAwait(false)
                       ?? throw new IOException($"The SharePoint site {spec.SiteUrl} was not found, or you have no access to it.");
            var siteId = site["id"]!.GetValue<string>();
            var siteName = site["displayName"]?.GetValue<string>() ?? sitePath;

            JsonNode drive;
            if (string.IsNullOrEmpty(library))
            {
                drive = await probe.GetJsonAsync($"{probe._graphBase}/sites/{siteId}/drive?$select=id,name,webUrl", ct).ConfigureAwait(false)
                        ?? throw new IOException("The site has no default document library.");
            }
            else
            {
                var drives = await probe.GetAllAsync($"{probe._graphBase}/sites/{siteId}/drives?$select=id,name,webUrl", ct).ConfigureAwait(false);
                drive = drives.FirstOrDefault(d => Matches(d, library))
                        ?? throw new IOException(
                            $"Document library '{library}' was not found. Available: {string.Join(", ", drives.Select(d => d["name"]?.GetValue<string>()))}.");
            }

            var driveName = drive["name"]?.GetValue<string>() ?? library;
            var webUrl = drive["webUrl"]?.GetValue<string>() ?? "";
            var prefix = Uri.TryCreate(webUrl, UriKind.Absolute, out var w) ? Uri.UnescapeDataString(w.AbsolutePath).Length : 0;
            log($"INFO: Connected to SharePoint site '{siteName}', library '{driveName}'");
            return new SharePointFileTree(graph, plain, tokens, log, graphBase, drive["id"]!.GetValue<string>(),
                $"SharePoint {siteName}/{driveName}", prefix);
        }
        catch
        {
            graph.Dispose();
            plain.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Splits a site URL into host, site path and (when a library URL was pasted) the library's URL name.
    /// "https://contoso.sharepoint.com/sites/Team/Shared Documents/Forms/AllItems.aspx" →
    /// ("contoso.sharepoint.com", "sites/Team", "Shared Documents").
    /// </summary>
    public static (string Host, string SitePath, string Library) ParseSiteUrl(string url)
    {
        var uri = new Uri(url.Trim());
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        int siteSegments;
        if (segments.Count >= 2 && segments[0].ToLowerInvariant() is "sites" or "teams" or "personal")
            siteSegments = 2;
        else if (segments.Count > 0 && segments[^1].EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
            siteSegments = 0;
        else
            siteSegments = segments.Count;

        var rest = segments.Skip(siteSegments).ToList();
        var library = rest.Count > 0 && !rest[0].EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)
                                     && !rest[0].Equals("_layouts", StringComparison.OrdinalIgnoreCase)
                                     && !rest[0].Equals("SitePages", StringComparison.OrdinalIgnoreCase)
            ? rest[0] : "";
        return (uri.Host, string.Join('/', segments.Take(siteSegments)), library);
    }

    private static bool Matches(JsonNode drive, string library)
    {
        if (string.Equals(drive["name"]?.GetValue<string>(), library, StringComparison.OrdinalIgnoreCase)) return true;
        var web = drive["webUrl"]?.GetValue<string>();
        if (web == null || !Uri.TryCreate(web, UriKind.Absolute, out var u)) return false;
        var last = Uri.UnescapeDataString(u.AbsolutePath.TrimEnd('/').Split('/')[^1]);
        return string.Equals(last, library, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Paths ------------------------------------------------------------------------------

    public string Combine(string directory, string name) =>
        directory.Trim('/').Length == 0 ? name : directory.TrimEnd('/') + "/" + name;

    public string GetName(string path)
    {
        var p = path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i < 0 ? p : p[(i + 1)..];
    }

    public string GetParent(string path)
    {
        var p = path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i <= 0 ? "" : p[..i];
    }

    public bool EndsWithSeparator(string path) => path.EndsWith('/');

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    /// <summary>Graph URL of the item at <paramref name="path"/>; <paramref name="action"/> e.g. "/children" or "/content".</summary>
    private string ItemUrl(string path, string action = "")
    {
        var p = path.Trim('/');
        return p.Length == 0 ? $"{_drive}/root{action}" : $"{_drive}/root:/{EscapePath(p)}" + (action.Length > 0 ? ":" + action : "");
    }

    // ---- IFileTree --------------------------------------------------------------------------

    public async Task<FileEntry?> StatAsync(string path, CancellationToken ct)
    {
        var item = await GetJsonAsync(ItemUrl(path) + "?$select=" + ItemSelect, ct).ConfigureAwait(false);
        return item == null ? null : ToEntry(item, path.Trim('/'));
    }

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken ct)
    {
        var items = await GetAllAsync(ItemUrl(directory, "/children") + "?$top=999&$select=" + ItemSelect, ct).ConfigureAwait(false);
        return items
            .Where(i => i["folder"] != null || i["file"] != null) // skip OneNote notebooks and other packages
            .Select(i => ToEntry(i, Combine(directory.Trim('/'), i["name"]!.GetValue<string>())))
            .ToList();
    }

    private static FileEntry ToEntry(JsonNode item, string path)
    {
        var isDir = item["folder"] != null;
        var modified = item["fileSystemInfo"]?["lastModifiedDateTime"]?.GetValue<string>()
                       ?? item["lastModifiedDateTime"]?.GetValue<string>();
        var time = modified != null
            ? DateTime.Parse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            : DateTime.MinValue;
        return new FileEntry(path, item["name"]?.GetValue<string>() ?? "", isDir, isDir ? 0 : item["size"]?.GetValue<long>() ?? 0, time);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        var p = path.Trim('/');
        if (p.Length == 0 || _knownDirectories.Contains(p)) return;
        var parent = GetParent(p);
        await CreateDirectoryAsync(parent, ct).ConfigureAwait(false);

        var existing = await StatAsync(p, ct).ConfigureAwait(false);
        if (existing == null)
        {
            if (SharePointNames.Problem(p, _prefixLength) is { } problem) throw new IOException(problem);
            var body = new JsonObject
            {
                ["name"] = GetName(p),
                ["folder"] = new JsonObject(),
                ["@microsoft.graph.conflictBehavior"] = "fail",
            };
            using var response = await SendAsync(() => JsonRequest(HttpMethod.Post, ItemUrl(parent, "/children"), body), _graph, true, ct)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Conflict) await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        }
        else if (!existing.IsDirectory)
        {
            throw new IOException($"'{p}' exists in SharePoint but is not a folder.");
        }

        _knownDirectories.Add(p);
    }

    public async Task SetLastWriteTimeUtcAsync(string path, DateTime utc, CancellationToken ct)
    {
        var body = new JsonObject { ["fileSystemInfo"] = new JsonObject { ["lastModifiedDateTime"] = Iso(utc) } };
        using var response = await SendAsync(() => JsonRequest(HttpMethod.Patch, ItemUrl(path), body), _graph, true, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a file (it goes to the SharePoint site's recycle bin).</summary>
    public async Task DeleteFileAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, ItemUrl(path)), _graph, true, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound) await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        // The pre-authenticated download URL is fetched fresh (it expires) and requested without a token.
        var item = await GetJsonAsync(ItemUrl(path) + "?$select=id,@microsoft.graph.downloadUrl", ct).ConfigureAwait(false)
                   ?? throw new FileNotFoundException($"'{path}' was not found in SharePoint.");
        var url = item["@microsoft.graph.downloadUrl"]?.GetValue<string>();
        var response = url != null
            ? await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), _plain, false, ct).ConfigureAwait(false)
            : await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ItemUrl(path, "/content")), _graph, true, ct).ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task ReadIntoAsync(string path, Stream destination, Action<long> progress, CancellationToken ct)
    {
        await using var source = await OpenReadAsync(path, ct).ConfigureAwait(false);
        await LocalFileTree.CopyWithProgressAsync(source, destination, progress, ct).ConfigureAwait(false);
    }

    public async Task WriteFromAsync(string path, Stream source, long length, DateTime lastWriteUtc, Action<long> progress, CancellationToken ct)
    {
        var p = path.Trim('/');
        if (SharePointNames.Problem(p, _prefixLength) is { } problem) throw new IOException(problem);

        if (length == 0)
        {
            using (var put = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, ItemUrl(p, "/content")) { Content = new ByteArrayContent([]) },
                       _graph, true, ct).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(put, ct).ConfigureAwait(false);
            }

            await SetLastWriteTimeUtcAsync(p, lastWriteUtc, ct).ConfigureAwait(false);
            progress(0);
            return;
        }

        var sessionBody = new JsonObject
        {
            ["item"] = new JsonObject
            {
                ["@microsoft.graph.conflictBehavior"] = "replace",
                ["fileSystemInfo"] = new JsonObject { ["lastModifiedDateTime"] = Iso(lastWriteUtc) },
            },
        };
        string uploadUrl;
        using (var session = await SendAsync(() => JsonRequest(HttpMethod.Post, ItemUrl(p, "/createUploadSession"), sessionBody), _graph, true, ct)
                   .ConfigureAwait(false))
        {
            await EnsureSuccessAsync(session, ct).ConfigureAwait(false);
            var json = JsonNode.Parse(await session.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            uploadUrl = json?["uploadUrl"]?.GetValue<string>() ?? throw new IOException("SharePoint did not return an upload URL.");
        }

        if (ChunkSize <= 0 || ChunkSize % ChunkUnit != 0) throw new InvalidOperationException("ChunkSize must be a multiple of 320 KiB.");
        var buffer = new byte[(int)Math.Min(ChunkSize, length)];
        long offset = 0;
        var completed = false;
        try
        {
            while (offset < length)
            {
                var size = (int)Math.Min(buffer.Length, length - offset);
                await source.ReadExactlyAsync(buffer.AsMemory(0, size), ct).ConfigureAwait(false);
                var start = offset;
                using var response = await SendAsync(() =>
                {
                    // The upload URL is pre-authenticated: never send the Graph token to it.
                    var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(buffer, 0, size) };
                    req.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + size - 1, length);
                    return req;
                }, _plain, false, ct).ConfigureAwait(false);
                await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
                offset += size;
                progress(offset);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created) completed = true;
            }

            if (!completed) throw new IOException("SharePoint did not confirm the end of the upload.");
        }
        catch
        {
            if (!completed)
            {
                try
                {
                    using var cancel = await _plain.SendAsync(new HttpRequestMessage(HttpMethod.Delete, uploadUrl), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // best-effort clean-up of the upload session
                }
            }

            throw;
        }
    }

    // ---- HTTP helpers ----------------------------------------------------------------------------

    private static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static HttpRequestMessage JsonRequest(HttpMethod method, string url, JsonNode body) =>
        new(method, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    /// <summary>GET a JSON object; null on 404.</summary>
    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), _graph, true, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>GET a collection, following @odata.nextLink.</summary>
    private async Task<List<JsonNode>> GetAllAsync(string url, CancellationToken ct)
    {
        var all = new List<JsonNode>();
        string? next = url;
        while (next != null)
        {
            var page = await GetJsonAsync(next, ct).ConfigureAwait(false)
                       ?? throw new FileNotFoundException("The SharePoint folder was not found.");
            if (page["value"] is JsonArray items) all.AddRange(items.Where(i => i != null)!);
            next = page["@odata.nextLink"]?.GetValue<string>();
        }

        return all;
    }

    /// <summary>
    /// Sends a request, retrying throttling (429, 503) and transient failures with Retry-After / back-off,
    /// and refreshing the token once on 401.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, HttpClient client, bool authenticate,
        CancellationToken ct)
    {
        var refreshed = false;
        for (var attempt = 1; ; attempt++)
        {
            using var request = create();
            if (authenticate)
            {
                var token = await _tokens.GetAccessTokenAsync(refreshed, ct).ConfigureAwait(false);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                var wait = Backoff(attempt);
                _log($"WARNING: network error talking to SharePoint ({ex.Message}); retrying in {wait.TotalSeconds:0} s");
                await Task.Delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && authenticate && !refreshed)
            {
                response.Dispose();
                refreshed = true;
                continue;
            }

            var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;
            if (!transient || attempt >= MaxAttempts) return response;

            var delay = RetryAfter(response) ?? Backoff(attempt);
            _log($"INFO: SharePoint is throttling ({(int)response.StatusCode}); waiting {delay.TotalSeconds:0} s before retrying");
            response.Dispose();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is { } delta) return delta > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : delta;
        if (ra?.Date is { } date)
        {
            var d = date - DateTimeOffset.UtcNow;
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }

        return null;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var code = response.ReasonPhrase ?? "";
        var message = "";
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var error = JsonNode.Parse(text)?["error"];
            code = error?["code"]?.GetValue<string>() ?? code;
            message = error?["message"]?.GetValue<string>() ?? text;
        }
        catch (JsonException)
        {
            // not a Graph error body
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
            message += " (check that the app registration has the Sites.ReadWrite.All / Sites.Selected permission with admin consent, and that you can access this site)";
        throw new GraphException(response.StatusCode, code, message);
    }

    public void Dispose()
    {
        _graph.Dispose();
        _plain.Dispose();
    }

    /// <summary>A response body stream that also disposes its HTTP response.</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
