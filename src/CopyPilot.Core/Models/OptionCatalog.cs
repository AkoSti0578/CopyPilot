using static CopyPilot.Core.Models.CommandCatalog;

namespace CopyPilot.Core.Models;

/// <summary>
/// Every azcopy v10 command-line flag CopyPilot exposes, with the commands it applies to.
/// Anything missing here (e.g. a flag added in a future azcopy release) can still be passed
/// through the "Additional arguments" field.
/// </summary>
public static class OptionCatalog
{
    private const string All = "*";

    private const string CatGlobal = "Global";
    private const string CatFilter = "Filters & selection";
    private const string CatBehavior = "Transfer behaviour";
    private const string CatIntegrity = "Integrity & validation";
    private const string CatPreserve = "Preserve properties";
    private const string CatBlob = "Blob properties";
    private const string CatHeaders = "Content headers";
    private const string CatS2S = "Service-to-service";
    private const string CatPerf = "Performance";
    private const string CatEncryption = "Encryption (CPK)";
    private const string CatDelete = "Delete";
    private const string CatList = "List";
    private const string CatMake = "Make";
    private const string CatBench = "Benchmark";
    private const string CatLogin = "Login";
    private const string CatJobs = "Jobs";
    private const string CatEnv = "Environment";
    private const string CatSftp = "SFTP & SharePoint (handled by CopyPilot)";

    public static IReadOnlyList<string> FromToValues { get; } =
    [
        "LocalBlob", "LocalBlobFS", "LocalFile", "LocalFileNFS",
        "BlobLocal", "BlobFSLocal", "FileLocal", "FileNFSLocal",
        "BlobBlob", "BlobFSBlobFS", "BlobBlobFS", "BlobFSBlob",
        "FileFile", "FileNFSFileNFS", "FileBlob", "BlobFile", "FileBlobFS", "BlobFSFile",
        "S3Blob", "GCPBlob",
        "BlobPipe", "PipeBlob",
        "BlobTrash", "FileTrash", "BlobFSTrash",
        "BlobNone", "FileNone", "BlobFSNone",
    ];

    private static string[] Cmds(params string[] ids) => ids;

    private static OptionDefinition Flag(string name, string cat, string desc, string[] cmds, string? def = null) =>
        new(name, OptionKind.Flag, cat, desc, cmds, def);

    private static OptionDefinition Text(string name, string cat, string desc, string[] cmds, string? def = null, bool sensitive = false) =>
        new(name, OptionKind.Text, cat, desc, cmds, def, Sensitive: sensitive);

    private static OptionDefinition Num(string name, string cat, string desc, string[] cmds, string? def = null) =>
        new(name, OptionKind.Number, cat, desc, cmds, def);

    private static OptionDefinition Choice(string name, string cat, string desc, string[] cmds, IReadOnlyList<string> choices, string? def = null) =>
        new(name, OptionKind.Choice, cat, desc, cmds, def, choices);

    private static OptionDefinition File(string name, string cat, string desc, string[] cmds) =>
        new(name, OptionKind.FilePath, cat, desc, cmds);

    private static OptionDefinition Date(string name, string cat, string desc, string[] cmds) =>
        new(name, OptionKind.DateTime, cat, desc, cmds);

    public static IReadOnlyList<OptionDefinition> Options { get; } =
    [
        // ---- Global -------------------------------------------------------------------------
        Num("cap-mbps", CatGlobal, "Caps the transfer rate, in megabits per second. Moment-by-moment throughput might vary slightly from the cap. 0 or empty = uncapped.", Cmds(All)),
        Choice("log-level", CatGlobal, "Log verbosity for the log file: DEBUG (detailed trace), INFO (all requests/responses), WARNING (slow responses), ERROR (only failed requests), NONE (no logs).", Cmds(All), ["DEBUG", "INFO", "WARNING", "ERROR", "NONE"], "INFO"),
        Choice("output-type", CatGlobal, "Format of the command's output: text, json or none. CopyPilot parses progress for both text and json.", Cmds(All), ["text", "json", "none"], "text"),
        Choice("output-level", CatGlobal, "Verbosity of the output: default, essential (only errors and summary) or quiet (nothing).", Cmds(All), ["default", "essential", "quiet"], "default"),
        Text("trusted-microsoft-suffixes", CatGlobal, "Additional domain suffixes (separated by ';') where Microsoft Entra login tokens may be sent.", Cmds(All)),
        Flag("skip-version-check", CatGlobal, "Do not check for a newer version of azcopy at startup.", Cmds(All)),

        // ---- Filters & selection ------------------------------------------------------------
        Flag("recursive", CatFilter, "Look into sub-directories recursively.", Cmds(Copy, Sync, Remove, SetProperties), "false (sync: true)"),
        Flag("as-subdir", CatFilter, "Place folder sources as sub-directories under the destination.", Cmds(Copy), "true"),
        Text("include-pattern", CatFilter, "Include only files whose name matches the pattern list, e.g. *.jpg;*.pdf;exactName", Cmds(Copy, Sync, Remove, SetProperties)),
        Text("exclude-pattern", CatFilter, "Exclude files whose name matches the pattern list, e.g. *.jpg;*.pdf;exactName", Cmds(Copy, Sync, Remove, SetProperties)),
        Text("include-path", CatFilter, "Include only these relative paths (files or directories), separated by ';'. No wildcards.", Cmds(Copy, Remove, SetProperties)),
        Text("exclude-path", CatFilter, "Exclude these relative paths (files or directories), separated by ';'. No wildcards.", Cmds(Copy, Sync, Remove, SetProperties)),
        Text("include-regex", CatFilter, "Include only relative paths that match any of these regular expressions (separated by ';').", Cmds(Copy, Sync)),
        Text("exclude-regex", CatFilter, "Exclude relative paths that match any of these regular expressions (separated by ';').", Cmds(Copy, Sync, Remove)),
        Date("include-before", CatFilter, "Include only files modified before or on this date/time, ISO-8601, e.g. 2026-01-31T12:00:00Z.", Cmds(Copy, Remove, SetProperties)),
        Date("include-after", CatFilter, "Include only files modified on or after this date/time, ISO-8601, e.g. 2026-01-31T12:00:00Z.", Cmds(Copy, Remove, SetProperties)),
        Text("include-attributes", CatFilter, "(Windows) Include only files whose attributes match, e.g. A;S;R", Cmds(Copy, Sync)),
        Text("exclude-attributes", CatFilter, "(Windows) Exclude files whose attributes match, e.g. A;S;R", Cmds(Copy, Sync)),
        Text("exclude-blob-type", CatFilter, "Blob types to exclude when copying from a container or account, e.g. BlockBlob;PageBlob;AppendBlob", Cmds(Copy)),
        Text("exclude-container", CatFilter, "(Account-to-account only) Container names to exclude, separated by ';'.", Cmds(Copy)),
        File("list-of-files", CatFilter, "A text file with one relative path per line to transfer / delete.", Cmds(Copy, Remove, SetProperties)),
        File("list-of-versions", CatFilter, "A text file with one version id per line; each must refer to a version of the source blob.", Cmds(Copy, Remove, SetProperties)),
        Flag("include-directory-stub", CatFilter, "Include directory stubs (blobs with metadata hdi_isfolder:true) in the transfer.", Cmds(Copy, Sync)),
        Flag("follow-symlinks", CatFilter, "Follow symbolic links when uploading from the local file system.", Cmds(Copy)),
        Flag("preserve-symlinks", CatFilter, "Preserve symbolic links (Linux / Blob / NFS). The link itself is copied, not the target.", Cmds(Copy, Sync)),
        Choice("hardlinks", CatFilter, "How hard links are handled for NFS transfers.", Cmds(Copy, Sync), ["follow", "skip", "preserve"], "follow"),

        // ---- Transfer behaviour ------------------------------------------------------------
        Choice("from-to", CatBehavior, "Explicitly specify the source-destination combination. Normally auto-detected; required for pipes, NFS and ambiguous cases.", Cmds(Copy, Sync, Remove, SetProperties), FromToValues),
        Choice("overwrite", CatBehavior, "Overwrite conflicting files/blobs at the destination: true, false, prompt or ifSourceNewer.", Cmds(Copy), ["true", "false", "prompt", "ifSourceNewer"], "true"),
        Choice("delete-destination", CatBehavior, "Delete extra files at the destination that are not present at the source: true, false or prompt.", Cmds(Sync), ["true", "false", "prompt"], "false"),
        Flag("delete-destination-file", CatBehavior, "Delete the destination file before uploading (useful for Azure Files when the file size changes).", Cmds(Sync)),
        Flag("mirror-mode", CatBehavior, "Disable last-modified comparison and overwrite conflicting files at the destination.", Cmds(Sync)),
        Flag("dry-run", CatBehavior, "Print which files would be copied/removed without actually doing it.", Cmds(Copy, Sync, Remove, SetProperties)),
        Flag("force-if-read-only", CatBehavior, "Force overwrite/delete of read-only files at an Azure Files or local Windows destination.", Cmds(Copy, Sync, Remove)),
        Flag("backup", CatBehavior, "(Windows) Activate SeBackupPrivilege / SeRestorePrivilege to read/write all files regardless of permissions.", Cmds(Copy, Sync)),
        Flag("decompress", CatBehavior, "Automatically decompress files when downloading if their Content-Encoding says they are compressed (gzip, deflate).", Cmds(Copy)),
        Flag("disable-auto-decoding", CatBehavior, "Disable automatic decoding of illegal Windows characters in file names.", Cmds(Copy, Sync, Remove)),
        Choice("trailing-dot", CatBehavior, "How to handle trailing dots in Azure Files names.", Cmds(Copy, Sync, Remove, List, SetProperties), ["Enable", "Disable", "AllowToUnsafeDestination"], "Enable"),
        Flag("nfs", CatBehavior, "Transfer to/from an NFS Azure file share (preserves NFS properties).", Cmds(Copy, Sync)),

        // ---- Integrity -------------------------------------------------------------------------
        Flag("check-length", CatIntegrity, "Check the destination file length after the transfer.", Cmds(Copy, Bench), "true"),
        Choice("check-md5", CatIntegrity, "How strictly MD5 hashes are validated when downloading.", Cmds(Copy, Sync), ["NoCheck", "LogOnly", "FailIfDifferent", "FailIfDifferentOrMissing"], "FailIfDifferent"),
        Flag("put-md5", CatIntegrity, "Create an MD5 hash of each file and save it as the Content-MD5 property of the destination blob/file.", Cmds(Copy, Sync, Bench)),
        Choice("compare-hash", CatIntegrity, "Compare files by hash instead of last-modified time.", Cmds(Sync), ["None", "MD5"], "None"),
        Choice("local-hash-storage-mode", CatIntegrity, "Where hashes of local files are stored for compare-hash.", Cmds(Sync), ["AlternateDataStreams", "HiddenFiles", "XAttr"]),
        new("hash-meta-dir", OptionKind.FolderPath, CatIntegrity, "Alternative directory for hidden hash files (with --local-hash-storage-mode=HiddenFiles).", Cmds(Sync)),

        // ---- Preserve -----------------------------------------------------------------------
        Flag("preserve-info", CatPreserve, "Preserve SMB/NFS property info (last write time, creation time, attributes).", Cmds(Copy, Sync)),
        Flag("preserve-smb-info", CatPreserve, "Preserve SMB property info (last write time, creation time, attribute bits) between SMB-aware resources.", Cmds(Copy, Sync), "true on Windows"),
        Flag("preserve-permissions", CatPreserve, "Preserve ACLs between aware resources (Windows / Azure Files / ADLS Gen2).", Cmds(Copy, Sync)),
        Flag("preserve-owner", CatPreserve, "With --preserve-permissions: also preserve owner and group.", Cmds(Copy), "true"),
        Flag("preserve-posix-properties", CatPreserve, "Preserve POSIX properties (Linux) in the object metadata.", Cmds(Copy, Sync)),
        Flag("preserve-last-modified-time", CatPreserve, "Preserve the last-modified time (only when the destination is a file system).", Cmds(Copy)),

        // ---- Blob properties -----------------------------------------------------------------
        Choice("blob-type", CatBlob, "Type of blob at the destination.", Cmds(Copy, Bench), ["Detect", "BlockBlob", "PageBlob", "AppendBlob"], "Detect"),
        Choice("block-blob-tier", CatBlob, "Access tier for block blobs.", Cmds(Copy, Sync, SetProperties), ["None", "Hot", "Cool", "Cold", "Archive"], "None"),
        Choice("page-blob-tier", CatBlob, "Access tier for page blobs (premium).", Cmds(Copy, SetProperties), ["None", "P4", "P6", "P10", "P15", "P20", "P30", "P40", "P50", "P60", "P70", "P80"], "None"),
        Choice("rehydrate-priority", CatBlob, "Rehydrate priority when changing an archived blob's tier.", Cmds(Copy, SetProperties), ["Standard", "High"], "Standard"),
        Text("metadata", CatBlob, "Metadata key-value pairs, e.g. key1=value1;key2=value2", Cmds(Copy, SetProperties)),
        Text("blob-tags", CatBlob, "Blob index tags, e.g. project=alpha&owner=arnold", Cmds(Copy, SetProperties)),

        // ---- Content headers ----------------------------------------------------------------
        Text("content-type", CatHeaders, "Content-Type of the destination (implies --no-guess-mime-type).", Cmds(Copy)),
        Text("content-encoding", CatHeaders, "Content-Encoding of the destination.", Cmds(Copy)),
        Text("content-language", CatHeaders, "Content-Language of the destination.", Cmds(Copy)),
        Text("content-disposition", CatHeaders, "Content-Disposition of the destination.", Cmds(Copy)),
        Text("cache-control", CatHeaders, "Cache-Control of the destination.", Cmds(Copy)),
        Flag("no-guess-mime-type", CatHeaders, "Do not detect the Content-Type from the file extension.", Cmds(Copy)),

        // ---- Service-to-service ---------------------------------------------------------------
        Flag("s2s-preserve-properties", CatS2S, "Preserve full properties during service-to-service copy.", Cmds(Copy), "true"),
        Flag("s2s-preserve-access-tier", CatS2S, "Preserve the access tier during service-to-service copy.", Cmds(Copy, Sync), "true"),
        Flag("s2s-preserve-blob-tags", CatS2S, "Preserve index tags during service-to-service copy between blob storage accounts.", Cmds(Copy, Sync)),
        Flag("s2s-detect-source-changed", CatS2S, "Detect whether the source changed while it was being read.", Cmds(Copy)),
        Choice("s2s-handle-invalid-metadata", CatS2S, "How to handle invalid metadata keys (e.g. from S3).", Cmds(Copy), ["ExcludeIfInvalid", "FailIfInvalid", "RenameIfInvalid"], "ExcludeIfInvalid"),

        // ---- Performance -----------------------------------------------------------------------
        Num("block-size-mb", CatPerf, "Block size in MiB for uploads/downloads (decimals allowed, e.g. 0.25). Auto-calculated when empty.", Cmds(Copy, Sync, Bench)),
        Num("put-blob-size-mb", CatPerf, "Largest size (MiB) uploaded with a single Put Blob request instead of blocks.", Cmds(Copy, Sync)),

        // ---- Encryption ------------------------------------------------------------------------
        Text("cpk-by-name", CatEncryption, "Customer-provided key by name: the encryption scope name.", Cmds(Copy, Sync, Remove)),
        Flag("cpk-by-value", CatEncryption, "Customer-provided key by value: the key and hash are read from CPK_ENCRYPTION_KEY and CPK_ENCRYPTION_KEY_SHA256 (see Environment).", Cmds(Copy, Sync, Remove)),

        // ---- Delete ----------------------------------------------------------------------------
        Choice("delete-snapshots", CatDelete, "Behaviour when a blob has snapshots: include (delete blob and snapshots) or only (only snapshots).", Cmds(Remove), ["include", "only"]),
        Choice("permanent-delete", CatDelete, "Permanently delete soft-deleted snapshots/versions.", Cmds(Remove), ["none", "snapshots", "versions", "snapshotsandversions"], "none"),

        // ---- List ------------------------------------------------------------------------------
        Flag("machine-readable", CatList, "Output sizes in bytes.", Cmds(List)),
        Flag("mega-units", CatList, "Display units in orders of 1000 instead of 1024.", Cmds(List)),
        Flag("running-tally", CatList, "Show the total number of files and their total size at the end.", Cmds(List)),
        Text("properties", CatList, "Extra properties to show, separated by ';': LastModifiedTime;VersionId;BlobType;BlobAccessTier;ContentType;ContentEncoding;ContentMD5;LeaseState;LeaseDuration;LeaseStatus;ArchiveStatus", Cmds(List)),

        // ---- Make ------------------------------------------------------------------------------
        Num("quota-gb", CatMake, "Maximum size of the new file share in GiB.", Cmds(Make)),

        // ---- Bench -----------------------------------------------------------------------------
        Choice("mode", CatBench, "Benchmark direction.", Cmds(Bench), ["Upload", "Download"], "Upload"),
        Num("file-count", CatBench, "Number of auto-generated data files.", Cmds(Bench), "100"),
        Text("size-per-file", CatBench, "Size of each generated file, e.g. 250M, 10G, 512K.", Cmds(Bench), "250M"),
        Num("number-of-folders", CatBench, "Put the files in this many folders (0 = no folders).", Cmds(Bench), "0"),
        Flag("delete-test-data", CatBench, "Delete the benchmark data at the end.", Cmds(Bench), "true"),

        // ---- Login -----------------------------------------------------------------------------
        Choice("login-type", CatLogin, "Login method: device (interactive browser code), spn (service principal), msi (managed identity), azcli, pscred (Azure PowerShell) or workload (workload identity).", Cmds(Login), ["device", "spn", "msi", "azcli", "pscred", "workload"], "device"),
        Text("tenant-id", CatLogin, "Microsoft Entra tenant id.", Cmds(Login)),
        Text("aad-endpoint", CatLogin, "Microsoft Entra endpoint (for sovereign clouds).", Cmds(Login)),
        Flag("identity", CatLogin, "Log in with a managed identity (legacy, same as --login-type=msi).", Cmds(Login)),
        Text("identity-client-id", CatLogin, "Client id of a user-assigned managed identity.", Cmds(Login)),
        Text("identity-object-id", CatLogin, "Object id of a user-assigned managed identity.", Cmds(Login)),
        Text("identity-resource-id", CatLogin, "Resource id of a user-assigned managed identity.", Cmds(Login)),
        Flag("service-principal", CatLogin, "Log in with a service principal (legacy, same as --login-type=spn). Put the secret in AZCOPY_SPA_CLIENT_SECRET.", Cmds(Login)),
        Text("application-id", CatLogin, "Application id of the service principal.", Cmds(Login)),
        File("certificate-path", CatLogin, "Path to the certificate for service principal login. Put the password in AZCOPY_SPA_CERT_PASSWORD.", Cmds(Login)),
        Flag("tenant", CatLogin, "Show the tenant id you are logged in to.", Cmds(LoginStatus)),
        Flag("endpoint", CatLogin, "Show the Entra endpoint you are logged in to.", Cmds(LoginStatus)),

        // ---- Jobs ------------------------------------------------------------------------------
        Choice("with-status", CatJobs, "Only include jobs / transfers with this status.", Cmds(JobsList, JobsShow, JobsClean),
            ["All", "Success", "Failed", "Cancelled", "Completed", "CompletedWithErrors", "CompletedWithSkipped", "CompletedWithErrorsAndSkipped", "InProgress"], "All"),
        Text("source-sas", CatJobs, "SAS token for the source (needed when resuming a job that used a SAS).", Cmds(JobsResume), sensitive: true),
        Text("destination-sas", CatJobs, "SAS token for the destination (needed when resuming a job that used a SAS).", Cmds(JobsResume), sensitive: true),
        Text("include", CatJobs, "Only resume transfers matching these patterns, separated by ';'.", Cmds(JobsResume)),
        Text("exclude", CatJobs, "Skip transfers matching these patterns, separated by ';'.", Cmds(JobsResume)),

        // ---- Env -------------------------------------------------------------------------------
        Flag("show-sensitive", CatEnv, "Show sensitive/secret environment variables.", Cmds(Env)),

        // ---- SFTP (CopyPilot settings, not azcopy flags) --------------------------------------
        new("sftp-staging-folder", OptionKind.FolderPath, CatSftp,
            "Local folder used to stage files when copying between SFTP / SharePoint and cloud storage. Default: %TEMP%\\CopyPilot\\staging.",
            Cmds(Copy, Sync), CopyPilotOnly: true),
        Num("sftp-staging-limit-mb", CatSftp,
            "Maximum size of the staging folder in MB. Each file is permanently deleted (like Shift+Delete, no Recycle Bin) as soon as it has been transferred. SFTP / SharePoint → cloud uploads in batches of up to this size; cloud → SFTP / SharePoint pauses azcopy while the limit is exceeded. 0 = one file at a time (slower for many small files).",
            Cmds(Copy, Sync), "256") with { CopyPilotOnly = true },
        Choice("sharepoint-preserve-metadata", CatSftp,
            "SharePoint destination: also set the library's visible columns. 'dates' sets Created and Modified to the source's dates; 'dates-and-authors' also sets Created By and Modified By (source must be SharePoint, same tenant). Uses SharePoint's ValidateUpdateListItem without creating an extra version. Needs the SharePoint permission AllSites.Write (user sign-in) or a certificate (app-only).",
            Cmds(Copy, Sync), ["none", "dates", "dates-and-authors"], "none") with { CopyPilotOnly = true },
        Num("sftp-timeout-seconds", CatSftp, "SSH connection timeout in seconds.", Cmds(Copy, Sync), "30") with { CopyPilotOnly = true },
    ];

    public static IEnumerable<OptionDefinition> For(string commandId) =>
        Options.Where(o => o.AppliesTo(commandId));
}
