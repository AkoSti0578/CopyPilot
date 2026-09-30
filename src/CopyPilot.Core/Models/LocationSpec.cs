using System.Text;

namespace CopyPilot.Core.Models;

public enum LocationKind
{
    Local,
    AzureBlob,
    AzureDataLakeGen2,
    AzureFiles,
    AmazonS3,
    GoogleCloudStorage,
    CustomUrl,
    /// <summary>Stream through stdin/stdout. CopyPilot connects the pipe to a local file.</summary>
    Pipe,
}

/// <summary>A source or destination, described by its parts. <see cref="Build"/> turns it into what azcopy expects.</summary>
public sealed class LocationSpec
{
    public LocationKind Kind { get; set; } = LocationKind.Local;

    /// <summary>Local file/folder path, or the file used for pipe transfers.</summary>
    public string LocalPath { get; set; } = "";

    /// <summary>Storage account name (Azure).</summary>
    public string Account { get; set; } = "";

    /// <summary>Container / file share / file system (Azure) or bucket (S3 / GCS). Empty = whole account.</summary>
    public string Container { get; set; } = "";

    /// <summary>Path inside the container. Wildcards (*) are allowed.</summary>
    public string Path { get; set; } = "";

    /// <summary>SAS token (with or without leading '?').</summary>
    public string Sas { get; set; } = "";

    /// <summary>Azure endpoint suffix, e.g. core.windows.net.</summary>
    public string EndpointSuffix { get; set; } = "core.windows.net";

    /// <summary>AWS region, e.g. eu-west-1. Empty = global endpoint.</summary>
    public string Region { get; set; } = "";

    /// <summary>Full URL for <see cref="LocationKind.CustomUrl"/>.</summary>
    public string Url { get; set; } = "";

    public static IReadOnlyList<string> AzureEndpointSuffixes { get; } =
    [
        "core.windows.net",
        "core.chinacloudapi.cn",
        "core.usgovcloudapi.net",
    ];

    public bool IsRemote => Kind is not (LocationKind.Local or LocationKind.Pipe);

    /// <summary>The service name azcopy uses in --from-to values.</summary>
    public string? FromToName => Kind switch
    {
        LocationKind.Local => "Local",
        LocationKind.AzureBlob => "Blob",
        LocationKind.AzureDataLakeGen2 => "BlobFS",
        LocationKind.AzureFiles => "File",
        LocationKind.AmazonS3 => "S3",
        LocationKind.GoogleCloudStorage => "GCP",
        LocationKind.Pipe => "Pipe",
        _ => null,
    };

    /// <summary>
    /// The positional argument for azcopy, or <c>null</c> for pipes (which have no positional argument).
    /// </summary>
    public string? Build()
    {
        switch (Kind)
        {
            case LocationKind.Local:
                return LocalPath.Trim();
            case LocationKind.Pipe:
                return null;
            case LocationKind.CustomUrl:
                return Url.Trim();
            case LocationKind.AzureBlob:
                return AzureUrl("blob");
            case LocationKind.AzureDataLakeGen2:
                return AzureUrl("dfs");
            case LocationKind.AzureFiles:
                return AzureUrl("file");
            case LocationKind.AmazonS3:
            {
                var host = string.IsNullOrWhiteSpace(Region)
                    ? "s3.amazonaws.com"
                    : $"s3.{Region.Trim()}.amazonaws.com";
                return Join($"https://{host}", Container, Path, "");
            }
            case LocationKind.GoogleCloudStorage:
                return Join("https://storage.cloud.google.com", Container, Path, "");
            default:
                throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null);
        }
    }

    private string AzureUrl(string service)
    {
        var suffix = string.IsNullOrWhiteSpace(EndpointSuffix) ? "core.windows.net" : EndpointSuffix.Trim().Trim('.');
        return Join($"https://{Account.Trim()}.{service}.{suffix}", Container, Path, Sas);
    }

    private static string Join(string root, string container, string path, string sas)
    {
        var sb = new StringBuilder(root);
        sb.Append('/');
        var c = container.Trim().Trim('/');
        if (c.Length > 0)
        {
            sb.Append(EscapePath(c));
            var p = path.Trim().Replace('\\', '/').TrimStart('/');
            if (p.Length > 0)
            {
                sb.Append('/').Append(EscapePath(p));
            }
        }

        var q = sas.Trim().TrimStart('?');
        if (q.Length > 0)
        {
            sb.Append('?').Append(q);
        }

        return sb.ToString();
    }

    /// <summary>Percent-encodes each path segment, keeping '/' and azcopy's '*' wildcard.</summary>
    public static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(seg => Uri.EscapeDataString(seg).Replace("%2A", "*")));

    /// <summary>Validation messages for this location, empty when valid.</summary>
    public IEnumerable<string> Validate(string label)
    {
        switch (Kind)
        {
            case LocationKind.Local:
                if (string.IsNullOrWhiteSpace(LocalPath)) yield return $"{label}: choose a local file or folder.";
                break;
            case LocationKind.Pipe:
                if (string.IsNullOrWhiteSpace(LocalPath)) yield return $"{label}: choose the file to stream through the pipe.";
                break;
            case LocationKind.CustomUrl:
                if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out _)) yield return $"{label}: enter a valid absolute URL.";
                break;
            case LocationKind.AzureBlob:
            case LocationKind.AzureDataLakeGen2:
            case LocationKind.AzureFiles:
                if (string.IsNullOrWhiteSpace(Account)) yield return $"{label}: enter the storage account name.";
                break;
            case LocationKind.AmazonS3:
            case LocationKind.GoogleCloudStorage:
                break; // an empty bucket means "whole account/project"
        }
    }

    public LocationSpec Clone() => (LocationSpec)MemberwiseClone();
}
