namespace CopyPilot.Core.Models;

/// <summary>
/// Environment variables that configure azcopy (authentication for Azure/AWS/GCP, performance,
/// logging, proxy, ...). CopyPilot sets them only for the azcopy process it launches.
/// </summary>
public static class EnvironmentCatalog
{
    private const string CatAzureAuth = "Azure authentication (Microsoft Entra ID)";
    private const string CatAws = "Amazon S3";
    private const string CatGcp = "Google Cloud Storage";
    private const string CatCpk = "Customer-provided keys";
    private const string CatPerf = "Performance";
    private const string CatPaths = "Logging & locations";
    private const string CatNet = "Network & misc";

    private static readonly string[] Any = ["*"];

    private static OptionDefinition V(string name, string cat, string desc, OptionKind kind = OptionKind.Text,
        bool sensitive = false, IReadOnlyList<string>? choices = null) =>
        new(name, kind, cat, desc, Any, null, choices, sensitive);

    public static IReadOnlyList<OptionDefinition> Variables { get; } =
    [
        V("AZCOPY_AUTO_LOGIN_TYPE", CatAzureAuth, "Log in automatically for every command instead of running 'login' first.", OptionKind.Choice,
            choices: ["DEVICE", "SPN", "MSI", "AZCLI", "PSCRED", "WORKLOAD"]),
        V("AZCOPY_TENANT_ID", CatAzureAuth, "Microsoft Entra tenant id used for auto-login."),
        V("AZCOPY_ACTIVE_DIRECTORY_ENDPOINT", CatAzureAuth, "Microsoft Entra endpoint (sovereign clouds)."),
        V("AZCOPY_SPA_APPLICATION_ID", CatAzureAuth, "Service principal application (client) id."),
        V("AZCOPY_SPA_CLIENT_SECRET", CatAzureAuth, "Service principal client secret.", sensitive: true),
        V("AZCOPY_SPA_CERT_PATH", CatAzureAuth, "Path to the service principal certificate (.pfx/.pem).", OptionKind.FilePath),
        V("AZCOPY_SPA_CERT_PASSWORD", CatAzureAuth, "Password of the service principal certificate.", sensitive: true),
        V("AZCOPY_MSI_CLIENT_ID", CatAzureAuth, "Client id of a user-assigned managed identity."),
        V("AZCOPY_MSI_OBJECT_ID", CatAzureAuth, "Object id of a user-assigned managed identity."),
        V("AZCOPY_MSI_RESOURCE_STRING", CatAzureAuth, "Resource id of a user-assigned managed identity."),

        V("AWS_ACCESS_KEY_ID", CatAws, "AWS access key id for S3 sources."),
        V("AWS_SECRET_ACCESS_KEY", CatAws, "AWS secret access key for S3 sources.", sensitive: true),
        V("AWS_SESSION_TOKEN", CatAws, "Optional AWS session token (temporary credentials).", sensitive: true),

        V("GOOGLE_APPLICATION_CREDENTIALS", CatGcp, "Path to the Google service account key file (JSON).", OptionKind.FilePath),
        V("GOOGLE_CLOUD_PROJECT", CatGcp, "Google Cloud project id (required when copying a whole account/project)."),

        V("CPK_ENCRYPTION_KEY", CatCpk, "Base64 AES-256 key for --cpk-by-value.", sensitive: true),
        V("CPK_ENCRYPTION_KEY_SHA256", CatCpk, "Base64 SHA-256 of the key for --cpk-by-value.", sensitive: true),

        V("AZCOPY_CONCURRENCY_VALUE", CatPerf, "Number of concurrent requests (default depends on CPU count). 'AUTO' enables auto-tuning.", OptionKind.Text),
        V("AZCOPY_CONCURRENT_FILES", CatPerf, "Number of files in progress at any one time.", OptionKind.Number),
        V("AZCOPY_CONCURRENT_SCAN", CatPerf, "Maximum parallelism used when scanning (listing) the source.", OptionKind.Number),
        V("AZCOPY_BUFFER_GB", CatPerf, "Maximum memory (GB) used for buffering data. Decimals allowed.", OptionKind.Number),
        V("AZCOPY_TUNE_TO_CPU", CatPerf, "Set to false to prevent auto-tuning to CPU usage.", OptionKind.Choice, choices: ["true", "false"]),
        V("AZCOPY_PARALLEL_STAT_FILES", CatPerf, "Call stat on local files in parallel while scanning (Linux).", OptionKind.Choice, choices: ["true", "false"]),
        V("AZCOPY_PACE_PAGE_BLOBS", CatPerf, "Pace page blob uploads to match the service's throughput.", OptionKind.Choice, choices: ["true", "false"]),
        V("AZCOPY_DISABLE_HIERARCHICAL_SCAN", CatPerf, "Use flat listing instead of hierarchical when scanning blob sources.", OptionKind.Choice, choices: ["true", "false"]),
        V("AZCOPY_SHOW_PERF_STATES", CatPerf, "Show performance/bottleneck states in the progress output.", OptionKind.Choice, choices: ["true", "false"]),
        V("AZCOPY_REQUEST_TRY_TIMEOUT", CatPerf, "Per-request try timeout in minutes.", OptionKind.Number),

        V("AZCOPY_LOG_LOCATION", CatPaths, "Folder where log files are written (default %USERPROFILE%\\.azcopy).", OptionKind.FolderPath),
        V("AZCOPY_JOB_PLAN_LOCATION", CatPaths, "Folder where job plan files are written (default %USERPROFILE%\\.azcopy).", OptionKind.FolderPath),
        V("AZCOPY_DOWNLOAD_TO_TEMP_PATH", CatPaths, "Download to a temporary file first and rename when complete.", OptionKind.Choice, choices: ["true", "false"]),
        V("AZCOPY_DISABLE_SYSLOG", CatPaths, "Disable logging to the system log / Windows event log.", OptionKind.Choice, choices: ["true", "false"]),

        V("HTTPS_PROXY", CatNet, "Proxy server, e.g. http://proxy.contoso.com:8080 (default: system proxy)."),
        V("NO_PROXY", CatNet, "Hosts that should bypass the proxy, comma separated."),
        V("AZCOPY_CACHE_PROXY_LOOKUP", CatNet, "Cache the proxy lookup result.", OptionKind.Choice, choices: ["true", "false"]),
        V("AZCOPY_USER_AGENT_PREFIX", CatNet, "Prefix added to the User-Agent header."),
        V("AZCOPY_CONTENT_TYPE_MAP", CatNet, "Path to a JSON file mapping file extensions to content types.", OptionKind.FilePath),
        V("AZCOPY_DEFAULT_SERVICE_API_VERSION", CatNet, "Override the storage service API version."),
    ];
}
