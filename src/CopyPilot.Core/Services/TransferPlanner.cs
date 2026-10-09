using System.Globalization;
using System.Text;
using CopyPilot.Core.Models;
using CopyPilot.Core.Sftp;

namespace CopyPilot.Core.Services;

/// <summary>How a transfer involving SFTP or SharePoint (which azcopy cannot reach) is carried out.</summary>
public enum SftpRoute
{
    /// <summary>Between local disks, SFTP and SharePoint, handled entirely by CopyPilot.</summary>
    Direct,

    /// <summary>SFTP / SharePoint → cloud: CopyPilot downloads to a staging folder, then azcopy uploads.</summary>
    SftpToCloud,

    /// <summary>Cloud → SFTP / SharePoint: azcopy downloads to a staging folder, then CopyPilot uploads.</summary>
    CloudToSftp,
}

/// <summary>What will be executed for a request: a plain azcopy command, or a CopyPilot (SFTP / SharePoint) transfer.</summary>
public sealed class ExecutionPlan
{
    public required CommandRequest Request { get; init; }

    /// <summary>The azcopy command when no SFTP or SharePoint location is involved.</summary>
    public CommandPlan? AzCopy { get; init; }

    public SftpRoute? Route { get; init; }
    public TreeCopyOptions? TreeOptions { get; init; }
    public string StagingRoot { get; init; } = "";

    /// <summary>Staging folder limit; files are deleted as soon as they are transferred. 0 = one file at a time.</summary>
    public long StagingLimitBytes { get; init; } = TransferPlanner.DefaultStagingLimitMb * 1024 * 1024;
    public TimeSpan SftpTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public List<string> Errors { get; } = [];

    /// <summary>Options that are set but have no effect on the CopyPilot part of the transfer.</summary>
    public List<string> Warnings { get; } = [];

    public bool IsValid => Errors.Count == 0;
    public bool IsSftp => Route != null;
    public bool NeedsAzCopy => AzCopy != null || Route is SftpRoute.SftpToCloud or SftpRoute.CloudToSftp;
    public bool CanExportScript => AzCopy != null;

    public string ToDisplayString(string exe = "azcopy") => Describe(exe, mask: true);

    public string ToUnmaskedString(string exe = "azcopy") => Describe(exe, mask: false);

    private string Describe(string exe, bool mask)
    {
        if (AzCopy != null) return mask ? AzCopy.ToDisplayString(exe) : AzCopy.ToUnmaskedString(exe);
        if (Route == null) return "";

        var src = Request.Source!;
        var dst = Request.Destination!;
        var sb = new StringBuilder();
        string Loc(LocationSpec l)
        {
            var built = l.Build() ?? "";
            return mask ? CommandBuilder.Mask(built) : built;
        }

        var staging = Path.Combine(StagingRoot, "<job>");
        switch (Route)
        {
            case SftpRoute.Direct:
                sb.Append("# CopyPilot ").Append(Request.Command.Id).Append(": ")
                    .Append(Loc(src)).Append(" → ").Append(Loc(dst)).AppendLine();
                sb.Append("#   ").Append(DescribeOptions());
                break;
            case SftpRoute.SftpToCloud:
            {
                sb.Append("# 1) CopyPilot download: ").Append(Loc(src)).Append(" → ").AppendLine(staging);
                var leg = TransferPlanner.BuildAzCopyLeg(this, new LocationSpec { Kind = LocationKind.Local, LocalPath = Path.Combine(staging, "<selection>") }, null);
                sb.Append("# 2) per batch: ").AppendLine(mask ? leg.ToDisplayString(exe) : leg.ToUnmaskedString(exe));
                sb.Append("# 3) permanently delete each batch from the staging folder (limit ").Append(StagingArea.Describe(StagingLimitBytes)).Append(')');
                break;
            }
            case SftpRoute.CloudToSftp:
            {
                var leg = TransferPlanner.BuildAzCopyLeg(this, null, new LocationSpec { Kind = LocationKind.Local, LocalPath = staging });
                sb.Append("# 1) ").AppendLine(mask ? leg.ToDisplayString(exe) : leg.ToUnmaskedString(exe));
                sb.Append("# 2) CopyPilot uploads each completed file to ").Append(Loc(dst))
                    .Append("  (").Append(DescribeOptions()).AppendLine(")");
                sb.Append("# 3) and permanently deletes it from the staging folder (limit ").Append(StagingArea.Describe(StagingLimitBytes)).Append("; azcopy is paused when it is exceeded)");
                break;
            }
        }

        return sb.ToString();
    }

    private string DescribeOptions()
    {
        var o = TreeOptions!;
        var parts = new List<string>();
        if (o.SyncMode) parts.Add("only new/changed files");
        if (o.Recursive) parts.Add("recursive");
        if (!o.AsSubdir && !o.SyncMode) parts.Add("as-subdir=false");
        if (o.IncludePatterns.Count > 0) parts.Add("include " + string.Join(";", o.IncludePatterns));
        if (o.ExcludePatterns.Count > 0) parts.Add("exclude " + string.Join(";", o.ExcludePatterns));
        if (!o.SyncMode) parts.Add("overwrite=" + o.Overwrite);
        if (o.DeleteDestination) parts.Add("delete extra destination files");
        if (o.DryRun) parts.Add("dry run");
        return string.Join(", ", parts);
    }
}

/// <summary>Turns a request into an <see cref="ExecutionPlan"/>. SFTP / SharePoint transfers are split into CopyPilot and azcopy legs.</summary>
public static class TransferPlanner
{
    /// <summary>azcopy options that CopyPilot's own transfer engine understands.</summary>
    private static readonly HashSet<string> TreeCopyOptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "recursive", "as-subdir", "include-pattern", "exclude-pattern", "overwrite", "delete-destination", "dry-run",
        "cap-mbps", "log-level", "output-type", "output-level", "trusted-microsoft-suffixes", "skip-version-check",
        "preserve-last-modified-time",
    };

    public static string DefaultStagingRoot => Path.Combine(Path.GetTempPath(), "CopyPilot", "staging");

    public const long DefaultStagingLimitMb = 256;

    public static ExecutionPlan Plan(CommandRequest request)
    {
        var cmd = request.Command;
        var src = request.Source;
        var dst = request.Destination;
        var copyPilotInvolved = cmd.Positionals == PositionalKind.SourceAndDestination
                                && (src?.IsCopyPilotHandled == true || dst?.IsCopyPilotHandled == true);
        if (!copyPilotInvolved)
        {
            var azPlan = CommandBuilder.Build(request);
            var p = new ExecutionPlan { Request = request, AzCopy = azPlan };
            p.Errors.AddRange(azPlan.Errors);
            return p;
        }

        var opts = request.Options
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        var isSync = cmd.Id == CommandCatalog.Sync;

        // Positional kind is SourceAndDestination here, and one side is set; the UI always passes both.
        src ??= new LocationSpec();
        dst ??= new LocationSpec();
        static bool Direct(LocationSpec l) => l.IsCopyPilotHandled || l.Kind == LocationKind.Local;
        var route = Direct(src) && Direct(dst) ? SftpRoute.Direct
            : src.IsCopyPilotHandled ? SftpRoute.SftpToCloud
            : SftpRoute.CloudToSftp;

        var tree = new TreeCopyOptions
        {
            Recursive = isSync ? !IsFalse(opts, "recursive") : IsTrue(opts, "recursive"),
            AsSubdir = !IsFalse(opts, "as-subdir"),
            IncludePatterns = TreeCopier.SplitPatterns(opts.GetValueOrDefault("include-pattern")),
            ExcludePatterns = TreeCopier.SplitPatterns(opts.GetValueOrDefault("exclude-pattern")),
            Overwrite = opts.GetValueOrDefault("overwrite")?.ToLowerInvariant() switch
            {
                "false" or "prompt" => OverwriteMode.Never,
                "ifsourcenewer" => OverwriteMode.IfSourceNewer,
                _ => OverwriteMode.Always,
            },
            SyncMode = isSync,
            DeleteDestination = isSync && string.Equals(opts.GetValueOrDefault("delete-destination"), "true", StringComparison.OrdinalIgnoreCase),
            DryRun = IsTrue(opts, "dry-run"),
        };

        var plan = new ExecutionPlan
        {
            Request = request,
            Route = route,
            TreeOptions = tree,
            StagingRoot = opts.GetValueOrDefault("sftp-staging-folder") is { Length: > 0 } s ? s : DefaultStagingRoot,
            StagingLimitBytes = (long)(ParseNumber(opts.GetValueOrDefault("sftp-staging-limit-mb")) is { } mb and >= 0 ? mb : DefaultStagingLimitMb) * 1024 * 1024,
            SftpTimeout = TimeSpan.FromSeconds(
                double.TryParse(opts.GetValueOrDefault("sftp-timeout-seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 30),
        };

        plan.Errors.AddRange(src.Validate("Source"));
        plan.Errors.AddRange(dst.Validate("Destination"));
        if (src.Kind == LocationKind.Pipe || dst.Kind == LocationKind.Pipe)
            plan.Errors.Add("Pipes cannot be combined with SFTP or SharePoint.");
        if (opts.GetValueOrDefault("overwrite") is "prompt" || opts.GetValueOrDefault("delete-destination") is "prompt")
            plan.Warnings.Add("'prompt' is not supported for SFTP / SharePoint and is treated as 'false'.");
        if (opts.GetValueOrDefault("sftp-timeout-seconds") is { Length: > 0 } ts && ParseNumber(ts) == null)
            plan.Errors.Add("sftp-timeout-seconds must be a number.");
        if (opts.GetValueOrDefault("sftp-staging-limit-mb") is { Length: > 0 } lim && ParseNumber(lim) is null or < 0)
            plan.Errors.Add("sftp-staging-limit-mb must be 0 or a positive number.");
        if (route == SftpRoute.SftpToCloud && tree.DeleteDestination)
            plan.Warnings.Add("--delete-destination is not supported when syncing from SFTP / SharePoint to cloud storage (files are uploaded in batches) and is ignored.");

        if (route == SftpRoute.Direct)
        {
            foreach (var name in opts.Keys.Where(k => !TreeCopyOptionNames.Contains(k)
                                                        && OptionCatalog.Options.FirstOrDefault(d => d.Name.Equals(k, StringComparison.OrdinalIgnoreCase)) is { CopyPilotOnly: false }))
            {
                plan.Warnings.Add($"--{name} has no effect on a transfer between local, SFTP and SharePoint locations.");
            }

            if (!string.IsNullOrWhiteSpace(request.ExtraArguments))
                plan.Warnings.Add("Additional arguments are ignored for a transfer between local, SFTP and SharePoint locations.");
        }
        else
        {
            // Validate the azcopy leg with a placeholder staging path.
            var placeholder = new LocationSpec { Kind = LocationKind.Local, LocalPath = Path.Combine(plan.StagingRoot, "job") };
            var leg = route == SftpRoute.SftpToCloud ? BuildAzCopyLeg(plan, placeholder, null) : BuildAzCopyLeg(plan, null, placeholder);
            plan.Errors.AddRange(leg.Errors.Where(e => !plan.Errors.Contains(e)));
        }

        return plan;
    }

    /// <summary>
    /// The azcopy part of a staged transfer. For SFTP / SharePoint → cloud the staged folder replaces the source and the user's
    /// command runs unchanged; for cloud → SFTP / SharePoint azcopy copies (never syncs) into the staging folder, keeping last-modified times.
    /// </summary>
    public static CommandPlan BuildAzCopyLeg(ExecutionPlan plan, LocationSpec? stagedSource, LocationSpec? stagingDestination)
    {
        var request = plan.Request;
        var userOptions = request.Options.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        if (stagedSource != null)
        {
            // Batches only contain part of the source, so azcopy must never delete "extra" destination files.
            if (userOptions.ContainsKey("delete-destination")) userOptions["delete-destination"] = "false";
            return CommandBuilder.Build(new CommandRequest
            {
                Command = request.Command,
                Source = stagedSource,
                Destination = request.Destination,
                Options = userOptions,
                ExtraArguments = request.ExtraArguments,
            });
        }

        var copy = CommandCatalog.Get(CommandCatalog.Copy);
        var options = userOptions
            .Where(kv => OptionCatalog.Options.Any(d => d.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) && d.AppliesTo(copy.Id) && !d.CopyPilotOnly))
            .Where(kv => kv.Key is not ("overwrite" or "as-subdir"))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        options["preserve-last-modified-time"] = "true";
        if (request.Command.Id == CommandCatalog.Sync && !IsFalse(userOptions, "recursive")) options["recursive"] = "true";
        if (plan.TreeOptions?.AsSubdir == false) options["as-subdir"] = "false";

        return CommandBuilder.Build(new CommandRequest
        {
            Command = copy,
            Source = request.Source,
            Destination = stagingDestination,
            Options = options,
            ExtraArguments = request.ExtraArguments,
        });
    }

    private static double? ParseNumber(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static bool IsTrue(Dictionary<string, string> o, string name) =>
        o.TryGetValue(name, out var v) && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalse(Dictionary<string, string> o, string name) =>
        o.TryGetValue(name, out var v) && string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
}
