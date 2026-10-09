using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CopyPilot.Core.Sftp;

public enum OverwriteMode
{
    Always,
    Never,
    IfSourceNewer,
}

public sealed class TreeCopyOptions
{
    public bool Recursive { get; init; }

    /// <summary>Copy a source folder as a sub-folder of the destination (azcopy's --as-subdir).</summary>
    public bool AsSubdir { get; init; } = true;

    public IReadOnlyList<string> IncludePatterns { get; init; } = [];
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];
    public OverwriteMode Overwrite { get; init; } = OverwriteMode.Always;

    /// <summary>Only copy files that are new or changed (size or newer last-modified time).</summary>
    public bool SyncMode { get; init; }

    /// <summary>With <see cref="SyncMode"/>: delete destination files that do not exist at the source.</summary>
    public bool DeleteDestination { get; init; }

    public bool DryRun { get; init; }
}

public sealed class TreeCopyResult
{
    public int Done { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Deleted { get; set; }
    public int Total { get; set; }
    public long Bytes { get; set; }

    /// <summary>Where the selected source ended up at the destination (file or folder).</summary>
    public string? DestinationSelection { get; set; }

    public bool SourceIsSingleFile { get; set; }

    public string Status => Failed == 0 ? (Skipped == 0 ? "Completed" : "CompletedWithSkipped")
        : Done == 0 ? "Failed" : "CompletedWithErrors";
}

/// <summary>A file selected for transfer, with its path relative to the destination root ('/'-separated).</summary>
public sealed record SelectedFile(FileEntry Entry, string RelativePath);

/// <summary>The files and folders a source path (file, folder or wildcard) selects.</summary>
public sealed class SourceSelection
{
    public required string Name { get; init; }
    public bool IsSingleFile { get; init; }
    public bool IsWildcard { get; init; }

    /// <summary>Relative folder that holds the selection at the destination ("" = the destination root itself).</summary>
    public string Prefix { get; init; } = "";

    public List<SelectedFile> Files { get; init; } = [];
    public List<string> Directories { get; init; } = [];
    public long TotalBytes => Files.Sum(f => f.Entry.Length);
}

public enum CopyOutcome
{
    Copied,
    Skipped,
    DryRun,
}

/// <summary>
/// Copies files between two <see cref="IFileTree"/>s (local ↔ SFTP ↔ SFTP), mirroring azcopy's
/// semantics for --recursive, --as-subdir, wildcards, include/exclude patterns, overwrite and sync.
/// Progress lines use azcopy's text format so the UI parses them the same way.
/// </summary>
public sealed class TreeCopier(Action<string> output)
{
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>Resolves what a source path selects (a file, a folder, or a wildcard in the last segment).</summary>
    public async Task<SourceSelection> ResolveAsync(IFileTree src, string srcPath, TreeCopyOptions o, CancellationToken ct)
    {
        var include = o.IncludePatterns.Select(GlobToRegex).ToList();
        var exclude = o.ExcludePatterns.Select(GlobToRegex).ToList();
        var name = src.GetName(srcPath);
        var files = new List<SelectedFile>();
        var dirs = new List<string>();

        if (name.IndexOfAny(['*', '?']) >= 0)
        {
            output($"INFO: Scanning {src.DisplayName} {srcPath} …");
            await WalkAsync(src, src.GetParent(srcPath), "", GlobToRegex(name), include, exclude, o.Recursive || o.SyncMode, files, dirs, ct)
                .ConfigureAwait(false);
            return new SourceSelection { Name = name, IsWildcard = true, Files = files, Directories = dirs };
        }

        var stat = await src.StatAsync(srcPath, ct).ConfigureAwait(false)
                   ?? throw new FileNotFoundException($"The source '{srcPath}' was not found on {src.DisplayName}.");
        if (!stat.IsDirectory)
        {
            if (Matches(stat.Name, include, exclude)) files.Add(new SelectedFile(stat, stat.Name));
            return new SourceSelection { Name = stat.Name, IsSingleFile = true, Files = files };
        }

        if (!o.Recursive && !o.SyncMode)
            throw new InvalidOperationException(
                "The source is a folder: set --recursive (Options tab) or use a wildcard such as folder/*.");

        output($"INFO: Scanning {src.DisplayName} {srcPath} …");
        await WalkAsync(src, srcPath, "", null, include, exclude, true, files, dirs, ct).ConfigureAwait(false);
        var prefix = o.AsSubdir && !o.SyncMode ? stat.Name : "";
        if (prefix.Length > 0)
        {
            files = files.Select(f => new SelectedFile(f.Entry, prefix + "/" + f.RelativePath)).ToList();
            dirs = dirs.Select(d => prefix + "/" + d).Prepend(prefix).ToList();
        }

        return new SourceSelection { Name = stat.Name, Prefix = prefix, Files = files, Directories = dirs };
    }

    /// <summary>Where a single-file selection goes: into the destination folder, or to the destination path as a file name.</summary>
    public static async Task<string> SingleFileTargetAsync(IFileTree dst, string dstPath, string name, CancellationToken ct)
    {
        var destStat = await dst.StatAsync(dstPath, ct).ConfigureAwait(false);
        var destIsDir = destStat?.IsDirectory == true || dst.EndsWithSeparator(dstPath);
        return destIsDir ? dst.Combine(dstPath, name) : dstPath;
    }

    public async Task<TreeCopyResult> CopyAsync(
        IFileTree src, string srcPath, IFileTree dst, string dstPath, TreeCopyOptions o, CancellationToken ct)
    {
        var selection = await ResolveAsync(src, srcPath, o, ct).ConfigureAwait(false);
        var result = new TreeCopyResult { SourceIsSingleFile = selection.IsSingleFile, Total = selection.Files.Count };
        var totalBytes = selection.TotalBytes;

        Func<SelectedFile, string> targetOf = item => JoinRelative(dst, dstPath, item.RelativePath);
        if (selection.IsSingleFile)
        {
            var single = await SingleFileTargetAsync(dst, dstPath, selection.Name, ct).ConfigureAwait(false);
            targetOf = _ => single;
            result.DestinationSelection = single;
        }
        else
        {
            result.DestinationSelection = selection.Prefix.Length > 0 ? JoinRelative(dst, dstPath, selection.Prefix) : dstPath;
            if (!o.DryRun)
            {
                await dst.CreateDirectoryAsync(dstPath, ct).ConfigureAwait(false);
                foreach (var d in selection.Directories)
                    await dst.CreateDirectoryAsync(JoinRelative(dst, dstPath, d), ct).ConfigureAwait(false);
            }
        }

        output($"INFO: {result.Total} file(s), {FormatBytes(totalBytes)} to process: {src.DisplayName} → {dst.DisplayName}");

        var progress = new ProgressReporter(output, result, totalBytes);
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in selection.Files)
        {
            ct.ThrowIfCancellationRequested();
            sourcePaths.Add(item.RelativePath);
            await CopyAndCountAsync(src, item.Entry, dst, targetOf(item), o, result, progress, ct).ConfigureAwait(false);
        }

        if (o.SyncMode && o.DeleteDestination && !selection.IsSingleFile)
            await DeleteExtrasAsync(dst, dstPath, sourcePaths, o.DryRun, result, ct).ConfigureAwait(false);

        progress.Report(force: true);
        output($"Transferred {FormatBytes(result.Bytes)} in {progress.Elapsed:hh\\:mm\\:ss}" + (result.Deleted > 0 ? $", deleted {result.Deleted} file(s)" : ""));
        return result;
    }

    /// <summary>Copies one file, updating the counters; failures are logged and counted, not thrown.</summary>
    public async Task<CopyOutcome?> CopyAndCountAsync(IFileTree src, FileEntry entry, IFileTree dst, string target,
        TreeCopyOptions o, TreeCopyResult result, ProgressReporter? progress, CancellationToken ct)
    {
        var before = result.Bytes;
        try
        {
            var outcome = await CopyOneAsync(src, entry, dst, target, o, b =>
            {
                result.Bytes = before + b;
                progress?.Report(force: false);
            }, ct).ConfigureAwait(false);
            switch (outcome)
            {
                case CopyOutcome.Skipped:
                    result.Skipped++;
                    break;
                case CopyOutcome.DryRun:
                    result.Done++;
                    break;
                default:
                    result.Bytes = before + entry.Length;
                    result.Done++;
                    break;
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Bytes = before;
            result.Failed++;
            output($"ERROR: failed to copy {entry.Path} → {target}: {ex.Message}");
            return null;
        }
        finally
        {
            progress?.FileProcessed(entry.Length);
        }
    }

    /// <summary>Copies one file (creating the parent folder), honouring overwrite / sync rules. Throws on failure.</summary>
    public async Task<CopyOutcome> CopyOneAsync(IFileTree src, FileEntry entry, IFileTree dst, string target,
        TreeCopyOptions o, Action<long> bytesProgress, CancellationToken ct)
    {
        var existing = await dst.StatAsync(target, ct).ConfigureAwait(false);
        if (existing?.IsDirectory == true)
            throw new IOException($"'{target}' is a directory at the destination.");
        if (ShouldSkip(entry, existing, o, dst.HasExactSizes && src.HasExactSizes)) return CopyOutcome.Skipped;

        if (o.DryRun)
        {
            output($"DRYRUN: copy {entry.Path} → {target}");
            return CopyOutcome.DryRun;
        }

        await dst.CreateDirectoryAsync(dst.GetParent(target), ct).ConfigureAwait(false);
        if (dst.IsLocal)
        {
            await using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await src.ReadIntoAsync(entry.Path, fs, bytesProgress, ct).ConfigureAwait(false);
        }
        else
        {
            await using var stream = await src.OpenReadAsync(entry.Path, ct).ConfigureAwait(false);
            await dst.WriteFromAsync(target, stream, entry.Length, entry.LastWriteUtc, bytesProgress, ct).ConfigureAwait(false);
        }

        if (!dst.SetsLastWriteTimeOnWrite)
            await dst.SetLastWriteTimeUtcAsync(target, entry.LastWriteUtc, ct).ConfigureAwait(false);
        return CopyOutcome.Copied;
    }

    /// <summary>Sync: deletes destination files whose relative path is not in <paramref name="keep"/>.</summary>
    public async Task DeleteExtrasAsync(IFileTree dst, string dstPath, ISet<string> keep, bool dryRun,
        TreeCopyResult result, CancellationToken ct)
    {
        if (await dst.StatAsync(dstPath, ct).ConfigureAwait(false) is not { IsDirectory: true }) return;
        var destFiles = new List<SelectedFile>();
        await WalkAsync(dst, dstPath, "", null, [], [], true, destFiles, [], ct).ConfigureAwait(false);
        foreach (var extra in destFiles.Where(f => !keep.Contains(f.RelativePath)))
        {
            ct.ThrowIfCancellationRequested();
            if (dryRun)
            {
                output($"DRYRUN: delete {extra.Entry.Path}");
                continue;
            }

            try
            {
                await dst.DeleteFileAsync(extra.Entry.Path, ct).ConfigureAwait(false);
                result.Deleted++;
                output($"INFO: deleted {extra.Entry.Path} (not present at the source)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                output($"ERROR: failed to delete {extra.Entry.Path}: {ex.Message}");
            }
        }
    }

    private static bool ShouldSkip(FileEntry source, FileEntry? existing, TreeCopyOptions o, bool compareSizes)
    {
        if (existing == null) return false;
        if (o.SyncMode)
            return (!compareSizes || existing.Length == source.Length) && existing.LastWriteUtc >= source.LastWriteUtc - TimeTolerance;
        return o.Overwrite switch
        {
            OverwriteMode.Never => true,
            OverwriteMode.IfSourceNewer => existing.LastWriteUtc >= source.LastWriteUtc - TimeTolerance,
            _ => false,
        };
    }

    private static async Task WalkAsync(IFileTree tree, string dir, string relative, Regex? topLevelPattern,
        List<Regex> include, List<Regex> exclude, bool recursive, List<SelectedFile> files, List<string> dirs, CancellationToken ct)
    {
        foreach (var entry in (await tree.ListAsync(dir, ct).ConfigureAwait(false)).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (topLevelPattern != null && !topLevelPattern.IsMatch(entry.Name)) continue;
            var rel = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
            if (entry.IsDirectory)
            {
                if (!recursive) continue;
                dirs.Add(rel);
                await WalkAsync(tree, entry.Path, rel, null, include, exclude, true, files, dirs, ct).ConfigureAwait(false);
            }
            else if (Matches(entry.Name, include, exclude))
            {
                files.Add(new SelectedFile(entry, rel));
            }
        }
    }

    private static bool Matches(string name, List<Regex> include, List<Regex> exclude) =>
        (include.Count == 0 || include.Any(r => r.IsMatch(name))) && !exclude.Any(r => r.IsMatch(name));

    /// <summary>Joins a '/'-separated relative path onto a destination root of the given tree.</summary>
    public static string JoinRelative(IFileTree tree, string root, string relative)
    {
        var path = root;
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries)) path = tree.Combine(path, part);
        return path;
    }

    /// <summary>Converts a azcopy-style wildcard (* and ?) into a case-insensitive regex.</summary>
    public static Regex GlobToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> SplitPatterns(string? value) =>
        (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{v:0.##} {units[u]}");
    }
}

/// <summary>Emits azcopy-style progress lines ("12.5 %, 3 Done, …") at most once per second.</summary>
public sealed class ProgressReporter(Action<string> output, TreeCopyResult result, long totalBytes)
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private TimeSpan _lastReport = TimeSpan.Zero;
    private long _bytesAtLastReport;
    private long _processedBytes;

    public TimeSpan Elapsed => _sw.Elapsed;

    /// <summary>Marks a file as processed (copied, skipped or failed) for the percentage.</summary>
    public void FileProcessed(long bytes)
    {
        _processedBytes += bytes;
        Report(force: false);
    }

    public void Report(bool force)
    {
        var now = _sw.Elapsed;
        if (!force && now - _lastReport < TimeSpan.FromSeconds(1)) return;
        var seconds = Math.Max((now - _lastReport).TotalSeconds, 0.001);
        var mbps = (result.Bytes - _bytesAtLastReport) * 8 / 1_000_000.0 / seconds;
        _lastReport = now;
        _bytesAtLastReport = result.Bytes;
        var processed = result.Done + result.Failed + result.Skipped;
        var pct = totalBytes > 0 ? 100.0 * _processedBytes / totalBytes
            : result.Total > 0 ? 100.0 * processed / result.Total : 100.0;
        output(string.Create(CultureInfo.InvariantCulture,
            $"{Math.Min(pct, 100):0.0} %, {result.Done} Done, {result.Failed} Failed, {Math.Max(result.Total - processed, 0)} Pending, {result.Skipped} Skipped, {result.Total} Total, 2-sec Throughput (Mb/s): {mbps:0.####}"));
    }
}
