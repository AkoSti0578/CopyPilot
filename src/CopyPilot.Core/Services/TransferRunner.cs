using CopyPilot.Core.Models;
using CopyPilot.Core.Sftp;

namespace CopyPilot.Core.Services;

/// <summary>
/// Executes an <see cref="ExecutionPlan"/>: plain azcopy commands, or SFTP transfers
/// (direct, or staged through a local folder combined with azcopy).
/// </summary>
public sealed class TransferRunner(AzCopyRunner azCopy)
{
    /// <summary>Folder/file that stores trusted SFTP host keys (null = do not verify).</summary>
    public string? KnownHostsFile { get; init; }

    /// <returns>0 on success, non-zero otherwise.</returns>
    public async Task<int> RunAsync(
        ExecutionPlan plan,
        string? azCopyExe,
        IReadOnlyDictionary<string, string> environment,
        Action<string, bool> onLine,
        CancellationToken ct)
    {
        if (plan.AzCopy != null)
            return await azCopy.RunAsync(RequireExe(azCopyExe), plan.AzCopy, environment, onLine, ct).ConfigureAwait(false);

        void Out(string line) => onLine(line, false);

        return plan.Route switch
        {
            SftpRoute.Direct => await RunDirectAsync(plan, Out, ct).ConfigureAwait(false),
            SftpRoute.SftpToCloud => await RunSftpToCloudAsync(plan, RequireExe(azCopyExe), environment, onLine, Out, ct).ConfigureAwait(false),
            SftpRoute.CloudToSftp => await RunCloudToSftpAsync(plan, RequireExe(azCopyExe), environment, onLine, Out, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Nothing to run."),
        };
    }

    private static string RequireExe(string? exe) =>
        exe ?? throw new InvalidOperationException("azcopy.exe was not found.");

    private async Task<IFileTree> OpenAsync(LocationSpec spec, ExecutionPlan plan, Action<string> output, CancellationToken ct)
    {
        if (!spec.IsSftp) return new LocalFileTree();
        output($"INFO: Connecting to {spec.Build()} …");
        var tree = await SftpFileTree.ConnectAsync(spec, KnownHostsFile, plan.SftpTimeout, output, ct).ConfigureAwait(false);
        output($"INFO: Connected to {tree.DisplayName}");
        return tree;
    }

    private static string PathOf(LocationSpec spec) => spec.IsSftp ? spec.SftpPath : spec.LocalPath.Trim();

    private static int Finish(TreeCopyResult r, Action<string> output)
    {
        output("Final Job Status: " + r.Status);
        return r.Failed == 0 ? 0 : 1;
    }

    private async Task<int> RunDirectAsync(ExecutionPlan plan, Action<string> output, CancellationToken ct)
    {
        var src = plan.Request.Source!;
        var dst = plan.Request.Destination!;
        using var srcTree = await OpenAsync(src, plan, output, ct).ConfigureAwait(false);
        using var dstTree = await OpenAsync(dst, plan, output, ct).ConfigureAwait(false);
        var result = await new TreeCopier(output)
            .CopyAsync(srcTree, PathOf(src), dstTree, PathOf(dst), plan.TreeOptions!, ct).ConfigureAwait(false);
        return Finish(result, output);
    }

    private async Task<int> RunSftpToCloudAsync(ExecutionPlan plan, string exe, IReadOnlyDictionary<string, string> env,
        Action<string, bool> onLine, Action<string> output, CancellationToken ct)
    {
        var src = plan.Request.Source!;
        var o = plan.TreeOptions!;
        var stage = CreateStagingFolder(plan, output);
        try
        {
            output("INFO: Step 1/2 – downloading from SFTP to the staging folder");
            TreeCopyResult staged;
            using (var srcTree = await OpenAsync(src, plan, output, ct).ConfigureAwait(false))
            {
                // Always keep the selected folder's name in the staging area; azcopy's own
                // --as-subdir / filters then apply exactly as they would for the original source.
                var stagingOptions = new TreeCopyOptions
                {
                    Recursive = o.Recursive,
                    AsSubdir = true,
                    IncludePatterns = o.IncludePatterns,
                    ExcludePatterns = o.ExcludePatterns,
                    DryRun = o.DryRun,
                };
                staged = await new TreeCopier(output)
                    .CopyAsync(srcTree, src.SftpPath, new LocalFileTree(), stage, stagingOptions, ct).ConfigureAwait(false);
            }

            if (staged.Failed > 0) return Finish(staged, output);
            if (o.DryRun)
            {
                output("INFO: Dry run – the azcopy upload step is skipped.");
                return Finish(staged, output);
            }

            // What azcopy should upload from the staging folder.
            var name = new SftpPathName(src.SftpPath);
            // single file → the staged file; folder → the staged folder (keeps its name for --as-subdir);
            // wildcard → the staging folder's contents.
            var stagedSource = !staged.SourceIsSingleFile && name.IsWildcard
                ? (plan.Request.Command.Id == CommandCatalog.Sync ? stage : Path.Combine(stage, "*"))
                : staged.DestinationSelection!;

            output("INFO: Step 2/2 – uploading with azcopy");
            var leg = TransferPlanner.BuildAzCopyLeg(plan, new LocationSpec { Kind = LocationKind.Local, LocalPath = stagedSource }, null);
            if (!leg.IsValid) throw new InvalidOperationException(string.Join(" ", leg.Errors));
            onLine("> " + leg.ToDisplayString(exe), false);
            return await azCopy.RunAsync(exe, leg, env, onLine, ct).ConfigureAwait(false);
        }
        finally
        {
            CleanUp(plan, stage, output);
        }
    }

    private async Task<int> RunCloudToSftpAsync(ExecutionPlan plan, string exe, IReadOnlyDictionary<string, string> env,
        Action<string, bool> onLine, Action<string> output, CancellationToken ct)
    {
        var dst = plan.Request.Destination!;
        var o = plan.TreeOptions!;
        var stage = CreateStagingFolder(plan, output);
        try
        {
            // Connect first so credential / host key problems show up before a long download.
            using var dstTree = await OpenAsync(dst, plan, output, ct).ConfigureAwait(false);

            output("INFO: Step 1/2 – downloading with azcopy to the staging folder");
            var leg = TransferPlanner.BuildAzCopyLeg(plan, null, new LocationSpec { Kind = LocationKind.Local, LocalPath = stage });
            if (!leg.IsValid) throw new InvalidOperationException(string.Join(" ", leg.Errors));
            onLine("> " + leg.ToDisplayString(exe), false);
            var code = await azCopy.RunAsync(exe, leg, env, onLine, ct).ConfigureAwait(false);
            if (code != 0)
            {
                output("ERROR: azcopy failed; nothing was uploaded to SFTP.");
                return code;
            }

            if (o.DryRun)
            {
                output("INFO: Dry run – the SFTP upload step is skipped.");
                return 0;
            }

            output("INFO: Step 2/2 – uploading to SFTP");
            // Upload the contents of the staging folder; a lone file keeps azcopy's "file to file" behaviour.
            var topFiles = Directory.GetFiles(stage);
            var topDirs = Directory.GetDirectories(stage);
            var uploadSource = topFiles.Length == 1 && topDirs.Length == 0 ? topFiles[0] : stage;
            var uploadOptions = new TreeCopyOptions
            {
                Recursive = true,
                AsSubdir = false,
                Overwrite = o.Overwrite,
                SyncMode = o.SyncMode,
                DeleteDestination = o.DeleteDestination,
            };
            var result = await new TreeCopier(output)
                .CopyAsync(new LocalFileTree(), uploadSource, dstTree, dst.SftpPath, uploadOptions, ct).ConfigureAwait(false);
            return Finish(result, output);
        }
        finally
        {
            CleanUp(plan, stage, output);
        }
    }

    private static string CreateStagingFolder(ExecutionPlan plan, Action<string> output)
    {
        var stage = Path.Combine(plan.StagingRoot, "job-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(stage);
        output($"INFO: Staging folder: {stage}");
        return stage;
    }

    private static void CleanUp(ExecutionPlan plan, string stage, Action<string> output)
    {
        if (plan.KeepStagedFiles)
        {
            output($"INFO: Staged files kept in {stage}");
            return;
        }

        try
        {
            Directory.Delete(stage, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output($"WARNING: could not delete the staging folder {stage}: {ex.Message}");
        }
    }

    private readonly record struct SftpPathName(string Path)
    {
        public bool IsWildcard => Path.TrimEnd('/').Split('/')[^1].IndexOfAny(['*', '?']) >= 0;
    }
}
