using System.Globalization;
using System.Text;
using CopyPilot.Core.Models;
using CopyPilot.Core.Sftp;

namespace CopyPilot.Core.Services;

/// <summary>How an SFTP transfer is carried out.</summary>
public enum SftpRoute
{
    /// <summary>Local ↔ SFTP or SFTP ↔ SFTP, handled entirely by CopyPilot.</summary>
    Direct,

    /// <summary>SFTP → cloud: CopyPilot downloads to a staging folder, then azcopy uploads.</summary>
    SftpToCloud,

    /// <summary>Cloud → SFTP: azcopy downloads to a staging folder, then CopyPilot uploads.</summary>
    CloudToSftp,
}

/// <summary>What will be executed for a request: a plain azcopy command, or an SFTP transfer.</summary>
public sealed class ExecutionPlan
{
    public required CommandRequest Request { get; init; }

    /// <summary>The azcopy command when no SFTP location is involved.</summary>
    public CommandPlan? AzCopy { get; init; }

    public SftpRoute? Route { get; init; }
    public TreeCopyOptions? TreeOptions { get; init; }
    public string StagingRoot { get; init; } = "";
    public bool KeepStagedFiles { get; init; }
    public TimeSpan SftpTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public List<string> Errors { get; } = [];

    /// <summary>Options that are set but have no effect on the SFTP part of the transfer.</summary>
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
                sb.Append("# CopyPilot SFTP ").Append(Request.Command.Id).Append(": ")
                    .Append(Loc(src)).Append(" → ").Append(Loc(dst)).AppendLine();
                sb.Append("#   ").Append(DescribeOptions());
                break;
            case SftpRoute.SftpToCloud:
            {
                sb.Append("# 1) CopyPilot SFTP download: ").Append(Loc(src)).Append(" → ").AppendLine(staging);
                var leg = TransferPlanner.BuildAzCopyLeg(this, new LocationSpec { Kind = LocationKind.Local, LocalPath = Path.Combine(staging, "<selection>") }, null);
                sb.Append("# 2) ").AppendLine(mask ? leg.ToDisplayString(exe) : leg.ToUnmaskedString(exe));
                sb.Append("# 3) ").Append(KeepStagedFiles ? "keep" : "delete").Append(" the staged files");
                break;
            }
            case SftpRoute.CloudToSftp:
            {
                var leg = TransferPlanner.BuildAzCopyLeg(this, null, new LocationSpec { Kind = LocationKind.Local, LocalPath = staging });
                sb.Append("# 1) ").AppendLine(mask ? leg.ToDisplayString(exe) : leg.ToUnmaskedString(exe));
                sb.Append("# 2) CopyPilot SFTP upload: ").Append(staging).Append(" → ").Append(Loc(dst))
                    .Append("  (").Append(DescribeOptions()).AppendLine(")");
                sb.Append("# 3) ").Append(KeepStagedFiles ? "keep" : "delete").Append(" the staged files");
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

/// <summary>Turns a request into an <see cref="ExecutionPlan"/>. SFTP transfers are split into CopyPilot and azcopy legs.</summary>
public static class TransferPlanner
{
    /// <summary>azcopy options that CopyPilot's own SFTP engine understands.</summary>
    private static readonly HashSet<string> TreeCopyOptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "recursive", "as-subdir", "include-pattern", "exclude-pattern", "overwrite", "delete-destination", "dry-run",
        "cap-mbps", "log-level", "output-type", "output-level", "trusted-microsoft-suffixes", "skip-version-check",
        "preserve-last-modified-time",
    };

    public static string DefaultStagingRoot => Path.Combine(Path.GetTempPath(), "CopyPilot", "staging");

    public static ExecutionPlan Plan(CommandRequest request)
    {
        var cmd = request.Command;
        var src = request.Source;
        var dst = request.Destination;
        var sftpInvolved = cmd.Positionals == PositionalKind.SourceAndDestination
                           && (src?.IsSftp == true || dst?.IsSftp == true);
        if (!sftpInvolved)
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

        var route = (src!.Kind, dst!.Kind) switch
        {
            (LocationKind.Sftp, LocationKind.Sftp or LocationKind.Local) => SftpRoute.Direct,
            (LocationKind.Local, LocationKind.Sftp) => SftpRoute.Direct,
            (LocationKind.Sftp, _) => SftpRoute.SftpToCloud,
            _ => SftpRoute.CloudToSftp,
        };

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
            KeepStagedFiles = IsTrue(opts, "sftp-keep-staged-files"),
            SftpTimeout = TimeSpan.FromSeconds(
                double.TryParse(opts.GetValueOrDefault("sftp-timeout-seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 30),
        };

        plan.Errors.AddRange(src.Validate("Source"));
        plan.Errors.AddRange(dst.Validate("Destination"));
        if (src.Kind == LocationKind.Pipe || dst.Kind == LocationKind.Pipe)
            plan.Errors.Add("Pipes cannot be combined with SFTP.");
        if (opts.GetValueOrDefault("overwrite") is "prompt" || opts.GetValueOrDefault("delete-destination") is "prompt")
            plan.Warnings.Add("'prompt' is not supported for SFTP and is treated as 'false'.");
        if (opts.GetValueOrDefault("sftp-timeout-seconds") is { Length: > 0 } ts && !double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            plan.Errors.Add("sftp-timeout-seconds must be a number.");

        if (route == SftpRoute.Direct)
        {
            foreach (var name in opts.Keys.Where(k => !TreeCopyOptionNames.Contains(k)
                                                        && OptionCatalog.Options.FirstOrDefault(d => d.Name.Equals(k, StringComparison.OrdinalIgnoreCase)) is { CopyPilotOnly: false }))
            {
                plan.Warnings.Add($"--{name} has no effect on a direct SFTP transfer.");
            }

            if (!string.IsNullOrWhiteSpace(request.ExtraArguments))
                plan.Warnings.Add("Additional arguments are ignored for a direct SFTP transfer.");
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
    /// The azcopy part of a staged SFTP transfer. For SFTP → cloud the staged folder replaces the source and the user's
    /// command runs unchanged; for cloud → SFTP azcopy copies (never syncs) into the staging folder, keeping last-modified times.
    /// </summary>
    public static CommandPlan BuildAzCopyLeg(ExecutionPlan plan, LocationSpec? stagedSource, LocationSpec? stagingDestination)
    {
        var request = plan.Request;
        var userOptions = request.Options.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        if (stagedSource != null)
        {
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

    private static bool IsTrue(Dictionary<string, string> o, string name) =>
        o.TryGetValue(name, out var v) && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalse(Dictionary<string, string> o, string name) =>
        o.TryGetValue(name, out var v) && string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
}
