using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CopyPilot.Core.Models;
using CopyPilot.Core.Sftp;

namespace CopyPilot.Core.SharePoint;

/// <summary>
/// Carries SharePoint's visible list item fields — Created, Modified, Created By (Author) and Modified By (Editor) —
/// to uploaded files with the SharePoint REST method ValidateUpdateListItem (bNewDocumentUpdate = true, so no extra
/// version is created).
/// <para>
/// ValidateUpdateListItem parses dates like the SharePoint UI: in the site's regional format and time zone. CopyPilot
/// therefore reads the site's locale, lets SharePoint convert UTC to site time (UTCToLocalTime), and reads the item back
/// to verify. If a format is not understood, it switches to an alternative format automatically.
/// </para>
/// </summary>
public sealed partial class SharePointFileTree
{
    private const int VerifyFirstItems = 25;
    private static readonly TimeSpan DateTolerance = TimeSpan.FromSeconds(61);

    private readonly ConcurrentDictionary<string, Task<WebRegional>> _regional = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private int _metadataFailures;

    /// <summary>Which list item fields to set on files uploaded to this library.</summary>
    public SharePointMetadataMode MetadataMode { get; set; } = SharePointMetadataMode.None;

    /// <summary>As a source: also read "Created By" / "Modified By" (needs SharePoint REST access).</summary>
    public bool ReadAuthors { get; set; }

    public bool WantsSourceMetadata => MetadataMode != SharePointMetadataMode.None;

    /// <summary>Files whose metadata could not (fully) be applied.</summary>
    public int MetadataFailures => _metadataFailures;

    private sealed record ItemRef(string WebUrl, string ListId, string ItemId)
    {
        public string Resource => "https://" + new Uri(WebUrl).Host;
        public string ItemApi => $"{WebUrl.TrimEnd('/')}/_api/web/lists(guid'{ListId}')/items({ItemId})";
    }

    private sealed class WebRegional
    {
        public required CultureInfo Culture { get; init; }
        public ConcurrentDictionary<DateTime, TimeSpan?> DayOffsets { get; } = new();
        public int FormatIndex;
        public int Verified;
    }

    // ---- Reading (source) ---------------------------------------------------------------------

    public async Task<FileMetadata?> GetMetadataAsync(FileEntry file, CancellationToken ct)
    {
        if (ReadAuthors)
        {
            var item = await GetItemRefAsync(file.Path, ct).ConfigureAwait(false);
            var fields = await ReadFieldsAsync(item, ct).ConfigureAwait(false);
            return fields with { ModifiedUtc = fields.ModifiedUtc ?? file.LastWriteUtc };
        }

        var graphItem = await GetJsonAsync(ItemUrl(file.Path) + "?$select=createdDateTime,fileSystemInfo", ct).ConfigureAwait(false);
        var created = ParseUtc(graphItem?["fileSystemInfo"]?["createdDateTime"]?.GetValue<string>()
                               ?? graphItem?["createdDateTime"]?.GetValue<string>());
        return new FileMetadata(created, file.LastWriteUtc, null, null);
    }

    private async Task<FileMetadata> ReadFieldsAsync(ItemRef item, CancellationToken ct)
    {
        var json = await SharePointRestAsync(HttpMethod.Get,
            item.ItemApi + "?$select=Created,Modified,Author/Name,Editor/Name&$expand=Author,Editor", null, item.Resource, ct).ConfigureAwait(false);
        return new FileMetadata(
            ParseUtc(json?["Created"]?.GetValue<string>()),
            ParseUtc(json?["Modified"]?.GetValue<string>()),
            json?["Author"]?["Name"]?.GetValue<string>(),
            json?["Editor"]?["Name"]?.GetValue<string>());
    }

    // ---- Writing (destination) ----------------------------------------------------------------

    public async Task ApplyMetadataAsync(string path, FileEntry source, FileMetadata? metadata, CancellationToken ct)
    {
        if (MetadataMode == SharePointMetadataMode.None || metadata == null) return;
        try
        {
            var item = await GetItemRefAsync(path, ct).ConfigureAwait(false);
            var regional = await _regional.GetOrAdd(item.WebUrl, w => LoadRegionalAsync(w, item.Resource, ct)).ConfigureAwait(false);
            var modified = metadata.ModifiedUtc ?? source.LastWriteUtc;
            var created = metadata.CreatedUtc is { } c && c <= modified ? c : modified;

            string? author = null, editor = null;
            if (MetadataMode == SharePointMetadataMode.DatesAndAuthors)
            {
                author = metadata.CreatedBy;
                editor = metadata.ModifiedBy ?? metadata.CreatedBy;
                if (author == null && editor == null)
                    WarnOnce("noauthors", "INFO: The source has no SharePoint authors; only Created and Modified are set (Created By / Modified By stay the account that uploaded).");
            }

            await SetFieldsAsync(item, regional, path, created, modified, author, editor, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _metadataFailures);
            WarnOnce("fail:" + ex.Message, $"WARNING: could not set Created/Modified{(MetadataMode == SharePointMetadataMode.DatesAndAuthors ? "/authors" : "")} on {path}: {ex.Message}");
        }
    }

    private async Task SetFieldsAsync(ItemRef item, WebRegional regional, string path, DateTime createdUtc, DateTime modifiedUtc,
        string? author, string? editor, CancellationToken ct)
    {
        var createdLocal = await ToSiteTimeAsync(item, regional, createdUtc, ct).ConfigureAwait(false);
        var modifiedLocal = await ToSiteTimeAsync(item, regional, modifiedUtc, ct).ConfigureAwait(false);

        while (true)
        {
            var format = regional.FormatIndex;
            var values = new List<(string Field, string Value)>
            {
                ("Created", FormatSiteDate(regional, createdLocal, format)),
                ("Modified", FormatSiteDate(regional, modifiedLocal, format)),
            };
            if (author != null) values.Add(("Author", PersonValue(author)));
            if (editor != null) values.Add(("Editor", PersonValue(editor)));

            var errors = await ValidateUpdateAsync(item, values, ct).ConfigureAwait(false);

            // A person that no longer exists: keep the uploader as author, but still set the dates.
            var personErrors = errors.Where(e => e.Field is "Author" or "Editor").ToList();
            if (personErrors.Count > 0)
            {
                foreach (var e in personErrors)
                    WarnOnce("person:" + (e.Field == "Author" ? author : editor),
                        $"WARNING: {(e.Field == "Author" ? author : editor)} could not be set as {(e.Field == "Author" ? "Created By" : "Modified By")} ({e.Message}); the uploading account is kept.");
                if (personErrors.Any(e => e.Field == "Author")) author = null;
                if (personErrors.Any(e => e.Field == "Editor")) editor = null;

                // SharePoint saves nothing when any field fails: send the dates and the people that are still valid again.
                values = values.Where(v => v.Field is "Created" or "Modified"
                                           || (v.Field == "Author" && author != null)
                                           || (v.Field == "Editor" && editor != null)).ToList();
                errors = await ValidateUpdateAsync(item, values, ct).ConfigureAwait(false);
            }

            var dateErrors = errors.Where(e => e.Field is "Created" or "Modified").ToList();
            var mismatch = false;
            if (dateErrors.Count == 0 && (regional.Verified < VerifyFirstItems))
            {
                var stored = await ReadFieldsAsync(item, ct).ConfigureAwait(false);
                mismatch = !Close(stored.CreatedUtc, createdUtc) || !Close(stored.ModifiedUtc, modifiedUtc);
                if (!mismatch) Interlocked.Increment(ref regional.Verified);
            }

            if (dateErrors.Count == 0 && !mismatch) return;

            // The site did not understand this date format (or understood it differently): try the next one.
            if (format + 1 < DateFormatCount)
            {
                if (Interlocked.CompareExchange(ref regional.FormatIndex, format + 1, format) == format)
                    _log($"INFO: SharePoint did not accept the {DescribeFormat(regional, format)} date format; switching to {DescribeFormat(regional, format + 1)}.");
                continue;
            }

            var detail = dateErrors.Count > 0 ? string.Join("; ", dateErrors.Select(e => $"{e.Field}: {e.Message}")) : "the stored dates differ from the source";
            throw new IOException($"the dates could not be set ({detail}).");
        }
    }

    private static bool Close(DateTime? actual, DateTime expected) =>
        actual is { } a && (a - expected).Duration() <= DateTolerance;

    private static string PersonValue(string loginName) => "[{'Key':'" + loginName.Replace("'", "\\'") + "'}]";

    private async Task<List<(string Field, string Message)>> ValidateUpdateAsync(ItemRef item, List<(string Field, string Value)> values,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["formValues"] = new JsonArray(values.Select(v => (JsonNode)new JsonObject { ["FieldName"] = v.Field, ["FieldValue"] = v.Value }).ToArray()),
            ["bNewDocumentUpdate"] = true,
        };
        var json = await SharePointRestAsync(HttpMethod.Post, item.ItemApi + "/ValidateUpdateListItem", body, item.Resource, ct).ConfigureAwait(false);
        var results = json?["value"] as JsonArray ?? json?["d"]?["ValidateUpdateListItem"]?["results"] as JsonArray ?? [];
        return results
            .Where(r => r?["HasException"]?.GetValue<bool>() == true)
            .Select(r => (r!["FieldName"]?.GetValue<string>() ?? "?", r["ErrorMessage"]?.GetValue<string>() ?? "error"))
            .ToList();
    }

    // ---- Site regional settings ---------------------------------------------------------------

    private const int DateFormatCount = 2;

    /// <summary>0 = the site's own short date and time format (like the UI), 1 = ISO "yyyy-MM-dd HH:mm:ss".</summary>
    private static string FormatSiteDate(WebRegional r, DateTime siteLocal, int format) => format == 0
        ? siteLocal.ToString(r.Culture.DateTimeFormat.ShortDatePattern + " " + r.Culture.DateTimeFormat.ShortTimePattern, r.Culture)
        : siteLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string DescribeFormat(WebRegional r, int format) => format == 0
        ? $"site's ({r.Culture.Name}, e.g. {FormatSiteDate(r, new DateTime(2026, 1, 31, 13, 45, 0), 0)})"
        : "ISO (e.g. 2026-01-31 13:45:00)";

    private async Task<WebRegional> LoadRegionalAsync(string webUrl, string resource, CancellationToken ct)
    {
        var json = await SharePointRestAsync(HttpMethod.Get, $"{webUrl.TrimEnd('/')}/_api/web/RegionalSettings?$select=LocaleId", null, resource, ct)
            .ConfigureAwait(false);
        var lcid = json?["LocaleId"]?.GetValue<int>() ?? json?["d"]?["LocaleId"]?.GetValue<int>() ?? 1033;
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(lcid);
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.GetCultureInfo("en-US");
        }

        _log($"INFO: SharePoint site regional settings: {culture.Name}; Created/Modified{(MetadataMode == SharePointMetadataMode.DatesAndAuthors ? " and authors" : "")} are carried over");
        return new WebRegional { Culture = culture };
    }

    /// <summary>
    /// Converts UTC to the site's time zone using SharePoint itself. Offsets are cached per day; on days with a
    /// daylight-saving switch every value is converted exactly.
    /// </summary>
    private async Task<DateTime> ToSiteTimeAsync(ItemRef item, WebRegional r, DateTime utc, CancellationToken ct)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var day = utc.Date;
        if (!r.DayOffsets.TryGetValue(day, out var offset))
        {
            var start = await UtcToSiteAsync(item, day, ct).ConfigureAwait(false) - day;
            var end = await UtcToSiteAsync(item, day.AddDays(1).AddSeconds(-1), ct).ConfigureAwait(false) - day.AddDays(1).AddSeconds(-1);
            offset = start == end ? start : null;
            r.DayOffsets[day] = offset;
        }

        return offset is { } o ? DateTime.SpecifyKind(utc + o, DateTimeKind.Unspecified) : await UtcToSiteAsync(item, utc, ct).ConfigureAwait(false);
    }

    private async Task<DateTime> UtcToSiteAsync(ItemRef item, DateTime utc, CancellationToken ct)
    {
        var iso = utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var json = await SharePointRestAsync(HttpMethod.Get,
            $"{item.WebUrl.TrimEnd('/')}/_api/web/RegionalSettings/TimeZone/UTCToLocalTime(@date)?@date='{iso}'", null, item.Resource, ct)
            .ConfigureAwait(false);
        var text = json?["value"]?.GetValue<string>() ?? json?["d"]?["UTCToLocalTime"]?.GetValue<string>()
                   ?? throw new IOException("SharePoint did not return the site's local time.");
        return DateTime.SpecifyKind(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified);
    }

    // ---- Helpers ------------------------------------------------------------------------------

    /// <summary>The SharePoint list, item and web (site or subsite) of a file, from Graph's sharepointIds.</summary>
    private async Task<ItemRef> GetItemRefAsync(string path, CancellationToken ct)
    {
        var json = await GetJsonAsync(ItemUrl(path) + "?$select=sharepointIds", ct).ConfigureAwait(false)
                   ?? throw new FileNotFoundException($"'{path}' was not found in SharePoint.");
        var ids = json["sharepointIds"] ?? throw new IOException("Graph did not return the SharePoint ids of the item.");
        return new ItemRef(
            ids["siteUrl"]?.GetValue<string>() ?? throw new IOException("Graph did not return the site URL of the item."),
            ids["listId"]?.GetValue<string>() ?? throw new IOException("Graph did not return the list id of the item."),
            ids["listItemId"]?.GetValue<string>() ?? throw new IOException("Graph did not return the list item id."));
    }

    private async Task<JsonNode?> SharePointRestAsync(HttpMethod method, string url, JsonNode? body, string resource, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Accept.ParseAdd("application/json;odata=nometadata");
            if (body != null)
            {
                req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
                req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json;odata=nometadata");
            }

            return req;
        }, _graph, resource, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private static DateTime? ParseUtc(string? value) => value == null
        ? null
        : DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private void WarnOnce(string key, string message)
    {
        bool first;
        lock (_warned) first = _warned.Add(key);
        if (first) _log(message);
    }
}
