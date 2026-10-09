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
    /// <summary>SFTP server (SSH). Handled by CopyPilot itself, as azcopy does not support SFTP.</summary>
    Sftp,
    /// <summary>SharePoint Online / OneDrive document library via Microsoft Graph. Handled by CopyPilot itself.</summary>
    SharePoint,
}

/// <summary>How CopyPilot signs in to Microsoft Graph for SharePoint.</summary>
public enum SharePointAuthMode
{
    /// <summary>Sign in as a user in the system browser (needs redirect URI http://localhost in the app registration).</summary>
    Interactive,
    /// <summary>Sign in as a user with a device code (shown in the output; works without a local browser).</summary>
    DeviceCode,
    /// <summary>App-only with a client secret (unattended).</summary>
    ClientSecret,
    /// <summary>App-only with a certificate (unattended, recommended over secrets).</summary>
    Certificate,
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

    /// <summary>SFTP host name or IP address.</summary>
    public string Host { get; set; } = "";

    /// <summary>SFTP port.</summary>
    public int Port { get; set; } = 22;

    /// <summary>SFTP user name.</summary>
    public string Username { get; set; } = "";

    /// <summary>SFTP password (secret). Also used for keyboard-interactive authentication.</summary>
    public string Password { get; set; } = "";

    /// <summary>Path to an OpenSSH / PuTTY private key file for SFTP.</summary>
    public string PrivateKeyPath { get; set; } = "";

    /// <summary>Passphrase of the private key (secret).</summary>
    public string PrivateKeyPassphrase { get; set; } = "";

    /// <summary>
    /// Expected SHA256 host key fingerprint ("SHA256:..."). Empty = trust on first use
    /// and remember the key in CopyPilot's known_hosts file.
    /// </summary>
    public string HostKeyFingerprint { get; set; } = "";

    /// <summary>SharePoint site URL, e.g. https://contoso.sharepoint.com/sites/Team (or a OneDrive URL).</summary>
    public string SiteUrl { get; set; } = "";

    /// <summary>Document library name (e.g. "Documents" / "Gedeelde documenten"). Empty = the site's default library.</summary>
    public string Library { get; set; } = "";

    /// <summary>Microsoft Entra tenant (id or domain, e.g. contoso.onmicrosoft.com). Empty = "organizations" (user sign-in only).</summary>
    public string TenantId { get; set; } = "";

    /// <summary>Application (client) id of the app registration CopyPilot signs in with.</summary>
    public string ClientId { get; set; } = "";

    public SharePointAuthMode SharePointAuth { get; set; } = SharePointAuthMode.Interactive;

    /// <summary>Client secret for <see cref="SharePointAuthMode.ClientSecret"/> (secret).</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Certificate file (.pfx) for <see cref="SharePointAuthMode.Certificate"/>.</summary>
    public string CertificatePath { get; set; } = "";

    /// <summary>Password of the certificate file (secret).</summary>
    public string CertificatePassword { get; set; } = "";

    public static IReadOnlyList<string> AzureEndpointSuffixes { get; } =
    [
        "core.windows.net",
        "core.chinacloudapi.cn",
        "core.usgovcloudapi.net",
    ];

    public bool IsRemote => Kind is not (LocationKind.Local or LocationKind.Pipe);

    public bool IsSftp => Kind == LocationKind.Sftp;

    public bool IsSharePoint => Kind == LocationKind.SharePoint;

    /// <summary>Locations azcopy cannot reach; CopyPilot transfers them itself (SFTP, SharePoint).</summary>
    public bool IsCopyPilotHandled => Kind is LocationKind.Sftp or LocationKind.SharePoint;

    /// <summary>Folder path inside the SharePoint library, '/'-separated, "" = library root.</summary>
    public string SharePointPath => Path.Trim().Replace('\\', '/').TrimStart('/');
    /// <summary>The SFTP path, "." (home directory) when empty.</summary>
    public string SftpPath => string.IsNullOrWhiteSpace(Path) ? "." : Path.Trim().Replace('\\', '/');

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
            case LocationKind.SharePoint:
            {
                // Display form only: SharePoint never goes to azcopy.
                var lib = string.IsNullOrWhiteSpace(Library) ? "(default library)" : Library.Trim();
                var path = SharePointPath;
                return $"{SiteUrl.Trim().TrimEnd('/')} » {lib}" + (path.Length > 0 ? " » " + path : "");
            }
            case LocationKind.Sftp:
            {
                // Display form only: SFTP never goes to azcopy.
                var user = string.IsNullOrWhiteSpace(Username) ? "" : Uri.EscapeDataString(Username.Trim()) + "@";
                var port = Port is 22 or <= 0 ? "" : ":" + Port;
                var path = SftpPath;
                var sep = path.StartsWith('/') ? "" : "/~/";
                if (path == ".") { path = ""; sep = "/~/"; }
                return $"sftp://{user}{Host.Trim()}{port}{sep}{path}";
            }
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
            case LocationKind.SharePoint:
                if (!Uri.TryCreate(SiteUrl.Trim(), UriKind.Absolute, out var site) || site.Scheme != Uri.UriSchemeHttps)
                    yield return $"{label}: enter the SharePoint site URL, e.g. https://contoso.sharepoint.com/sites/Team.";
                if (!Guid.TryParse(ClientId.Trim(), out _))
                    yield return $"{label}: enter the application (client) id of your app registration.";
                if (SharePointAuth is SharePointAuthMode.ClientSecret or SharePointAuthMode.Certificate)
                {
                    if (string.IsNullOrWhiteSpace(TenantId))
                        yield return $"{label}: app-only sign-in needs the tenant id or domain.";
                    if (SharePointAuth == SharePointAuthMode.ClientSecret && string.IsNullOrEmpty(ClientSecret))
                        yield return $"{label}: enter the client secret.";
                    if (SharePointAuth == SharePointAuthMode.Certificate && string.IsNullOrWhiteSpace(CertificatePath))
                        yield return $"{label}: choose the certificate (.pfx) file.";
                }

                break;
            case LocationKind.Sftp:
                if (string.IsNullOrWhiteSpace(Host)) yield return $"{label}: enter the SFTP host.";
                if (Port is <= 0 or > 65535) yield return $"{label}: the SFTP port must be between 1 and 65535.";
                if (string.IsNullOrWhiteSpace(Username)) yield return $"{label}: enter the SFTP user name.";
                if (string.IsNullOrEmpty(Password) && string.IsNullOrWhiteSpace(PrivateKeyPath))
                    yield return $"{label}: enter an SFTP password or choose a private key file.";
                break;
        }
    }

    public LocationSpec Clone() => (LocationSpec)MemberwiseClone();
}
