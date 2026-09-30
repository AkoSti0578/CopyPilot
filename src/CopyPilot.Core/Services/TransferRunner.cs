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

    // ------------------------------------------------------------------------------------------
    // SFTP → cloud: download a batch into the staging folder, let azcopy upload it, then permanently
    // delete the batch. The staging folder never holds much more than the staging limit.
    // ------------------------------------------------------------------------------------------
    private async Task<int> RunSftpToCloudAsync(ExecutionPlan plan, string exe, IReadOnlyDictionary<string, string> env,
        Action<string, bool> onLine, Action<string> output, CancellationToken ct)
    {
        var src = plan.Request.Source!;
        var o = plan.TreeOptions!;
        var isSync = plan.Request.Command.Id == CommandCatalog.Sync;
        var staging = new StagingArea(plan.StagingRoot, output);
        try
        {
            using var srcTree = await OpenAsync(src, plan, output, ct).ConfigureAwait(false);
            var local = new LocalFileTree();
            var copier = new TreeCopier(output);

            // Keep the selected folder's name in the staging area, so azcopy's own --as-subdir and
            // filters behave exactly as they would for the original source.
            var stagingOptions = new TreeCopyOptions
            {
                Recursive = o.Recursive,
                AsSubdir = true,
                IncludePatterns = o.IncludePatterns,
                ExcludePatterns = o.ExcludePatterns,
                DryRun = o.DryRun,
            };
            if (o.DryRun)
            {
                var dry = await copier.CopyAsync(srcTree, src.SftpPath, local, staging.Folder, stagingOptions, ct).ConfigureAwait(false);
                output("INFO: Dry run – nothing was downloaded or uploaded.");
                return Finish(dry, output);
            }

            var selection = await copier.ResolveAsync(srcTree, src.SftpPath, stagingOptions, ct).ConfigureAwait(false);
            var stagedSource = selection.IsSingleFile ? Path.Combine(staging.Folder, selection.Name)
                : selection.IsWildcard ? (isSync ? staging.Folder : Path.Combine(staging.Folder, "*"))
                : Path.Combine(staging.Folder, selection.Name);
            var leg = TransferPlanner.BuildAzCopyLeg(plan, new LocationSpec { Kind = LocationKind.Local, LocalPath = stagedSource }, null);
            if (!leg.IsValid) throw new InvalidOperationException(string.Join(" ", leg.Errors));

            var result = new TreeCopyResult { Total = selection.Files.Count, SourceIsSingleFile = selection.IsSingleFile };
            var progress = new ProgressReporter(output, result, selection.TotalBytes);
            output($"INFO: {result.Total} file(s), {TreeCopier.FormatBytes(selection.TotalBytes)}; staging limit {StagingArea.Describe(plan.StagingLimitBytes)}");
            onLine("> " + leg.ToDisplayString(exe) + "   (run per batch)", false);

            var batch = new List<(string Staged, long Length)>();
            var batchNo = 0;

            async Task<int> FlushAsync()
            {
                if (batch.Count == 0) return 0;
                batchNo++;
                var bytes = batch.Sum(b => b.Length);
                output($"INFO: Batch {batchNo}: uploading {batch.Count} file(s), {TreeCopier.FormatBytes(bytes)} with azcopy …");
                var code = await azCopy.RunAsync(exe, leg, env, BatchOutput(onLine), ct).ConfigureAwait(false);
                foreach (var (staged, length) in batch)
                {
                    staging.Delete(staged, pruneEmptyFolders: true);
                    if (code == 0)
                    {
                        result.Done++;
                        progress.FileProcessed(length);
                    }
                    else
                    {
                        result.Failed++;
                    }
                }

                batch.Clear();
                progress.Report(force: true);
                return code;
            }

            foreach (var file in selection.Files)
            {
                ct.ThrowIfCancellationRequested();
                var stagedPath = selection.IsSingleFile
                    ? Path.Combine(staging.Folder, selection.Name)
                    : TreeCopier.JoinRelative(local, staging.Folder, file.RelativePath);
                var download = new TreeCopyResult();
                if (await copier.CopyAndCountAsync(srcTree, file.Entry, local, stagedPath, new TreeCopyOptions(), download, null, ct)
                        .ConfigureAwait(false) == null)
                {
                    result.Failed++;
                    progress.FileProcessed(file.Entry.Length);
                    staging.Delete(stagedPath);
                    continue;
                }

                staging.Added(file.Entry.Length);
                batch.Add((stagedPath, file.Entry.Length));
                if (staging.CurrentBytes >= plan.StagingLimitBytes || batch.Count >= StagingArea.MaxFilesPerBatch)
                {
                    var code = await FlushAsync().ConfigureAwait(false);
                    if (code != 0)
                    {
                        output($"ERROR: azcopy failed (exit code {code}); stopping. Files already uploaded stay in place.");
                        Finish(result, output);
                        return code;
                    }
                }
            }

            var last = await FlushAsync().ConfigureAwait(false);
            output($"INFO: Peak staging usage: {TreeCopier.FormatBytes(staging.PeakBytes)}");
            var status = Finish(result, output);
            return last != 0 ? last : status;
        }
        finally
        {
            staging.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------
    // Cloud → SFTP: azcopy downloads into the staging folder (to temporary names, renamed when a file
    // is complete). Every completed file is uploaded to SFTP right away and permanently deleted.
    // When the staging folder exceeds the limit, azcopy is paused until the upload catches up.
    // ------------------------------------------------------------------------------------------
    private async Task<int> RunCloudToSftpAsync(ExecutionPlan plan, string exe, IReadOnlyDictionary<string, string> env,
        Action<string, bool> onLine, Action<string> output, CancellationToken ct)
    {
        var dst = plan.Request.Destination!;
        var o = plan.TreeOptions!;
        var staging = new StagingArea(plan.StagingRoot, output);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<int>? azTask = null;
        try
        {
            // Connect first so credential / host key problems show up before any download.
            using var dstTree = await OpenAsync(dst, plan, output, ct).ConfigureAwait(false);

            var leg = TransferPlanner.BuildAzCopyLeg(plan, null, new LocationSpec { Kind = LocationKind.Local, LocalPath = staging.Folder });
            if (!leg.IsValid) throw new InvalidOperationException(string.Join(" ", leg.Errors));
            var legEnv = new Dictionary<string, string>(env) { [StagingArea.TempPathVariable] = "true" };
            onLine("> " + leg.ToDisplayString(exe), false);

            if (o.DryRun)
            {
                var dryCode = await azCopy.RunAsync(exe, leg, legEnv, onLine, ct).ConfigureAwait(false);
                output("INFO: Dry run – nothing was uploaded to SFTP.");
                return dryCode;
            }

            output($"INFO: Downloading with azcopy and uploading each completed file to SFTP (staging limit {StagingArea.Describe(plan.StagingLimitBytes)})");
            var local = new LocalFileTree();
            var copier = new TreeCopier(output);
            var uploadOptions = new TreeCopyOptions { Overwrite = o.Overwrite, SyncMode = o.SyncMode };
            var result = new TreeCopyResult();
            var processed = new HashSet<string>(StringComparer.Ordinal);
            var watcher = new StagingWatcher(staging.Folder);
            var lastStatus = DateTime.UtcNow;
            var pausedLogged = false;

            azTask = Task.Run(() => azCopy.RunAsync(exe, leg, legEnv, onLine, linked.Token), CancellationToken.None);
            while (true)
            {
                var finished = azTask.IsCompleted;
                var ready = watcher.Scan(requireStable: !finished);
                staging.Observe(watcher.StagedBytes);

                // Back-pressure: pause azcopy while completed files wait for the (slower) SFTP upload.
                if (!finished && ready.Count > 0 && watcher.StagedBytes > plan.StagingLimitBytes && !azCopy.IsSuspended && azCopy.Suspend() && !pausedLogged)
                {
                    output("INFO: Staging limit reached – azcopy is paused while files are uploaded to SFTP (this repeats silently).");
                    pausedLogged = true;
                }

                foreach (var file in ready)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(staging.Folder, file.Path).Replace('\\', '/');
                    var target = TreeCopier.JoinRelative(dstTree, dst.SftpPath, rel);
                    result.Total++;
                    processed.Add(rel);
                    await copier.CopyAndCountAsync(local, file, dstTree, target, uploadOptions, result, null, ct).ConfigureAwait(false);
                    staging.Delete(file.Path);
                    watcher.Forget(file.Path);

                    if (azCopy.IsSuspended && watcher.StagedBytesAfterDelete(file.Length) <= plan.StagingLimitBytes / 2) azCopy.Resume();
                }

                if (azCopy.IsSuspended) azCopy.Resume(); // nothing left to upload: let azcopy continue
                if (DateTime.UtcNow - lastStatus > TimeSpan.FromSeconds(2) && result.Total > 0)
                {
                    lastStatus = DateTime.UtcNow;
                    output($"INFO: SFTP upload: {result.Done} done, {result.Skipped} skipped, {result.Failed} failed, {TreeCopier.FormatBytes(result.Bytes)}");
                }

                if (finished && ready.Count == 0) break;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }

            var code = await azTask.ConfigureAwait(false);
            if (code == 0 && o.SyncMode && o.DeleteDestination)
                await copier.DeleteExtrasAsync(dstTree, dst.SftpPath, processed, false, result, ct).ConfigureAwait(false);
            else if (code != 0 && o.DeleteDestination)
                output("WARNING: azcopy failed, so no files were deleted at the SFTP destination.");

            new ProgressReporter(output, result, 0).Report(force: true);
            output($"INFO: Uploaded {TreeCopier.FormatBytes(result.Bytes)} to SFTP; peak staging usage {TreeCopier.FormatBytes(staging.PeakBytes)}");
            var status = Finish(result, output);
            return code != 0 ? code : status;
        }
        finally
        {
            azCopy.Resume();
            if (azTask is { IsCompleted: false })
            {
                await linked.CancelAsync().ConfigureAwait(false);
                try { await azTask.ConfigureAwait(false); } catch (Exception) { /* already reported */ }
            }

            staging.Dispose();
        }
    }

    /// <summary>Hides azcopy's per-batch progress lines; CopyPilot reports overall progress instead.</summary>
    private static Action<string, bool> BatchOutput(Action<string, bool> onLine) => (line, isErr) =>
    {
        var parsed = OutputParser.Parse(line);
        if (parsed.Progress?.Percent != null || parsed.Progress?.JobStatus != null) return;
        onLine(line, isErr);
    };
}
