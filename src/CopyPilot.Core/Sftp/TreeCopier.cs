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

/// <summary>
/// Copies files between two <see cref="IFileTree"/>s (local ↔ SFTP ↔ SFTP), mirroring azcopy's
/// semantics for --recursive, --as-subdir, wildcards, include/exclude patterns, overwrite and sync.
/// Progress lines use azcopy's text format so the UI parses them the same way.
/// </summary>
public sealed class TreeCopier(Action<string> output)
{
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    private sealed record Item(FileEntry Entry, string RelativePath);

    public async Task<TreeCopyResult> CopyAsync(
        IFileTree src, string srcPath, IFileTree dst, string dstPath, TreeCopyOptions o, CancellationToken ct)
    {
        var result = new TreeCopyResult();
        var include = o.IncludePatterns.Select(GlobToRegex).ToList();
        var exclude = o.ExcludePatterns.Select(GlobToRegex).ToList();

        // ---- 1. Resolve the source selection -------------------------------------------------
        var name = src.GetName(srcPath);
        Regex? topLevelPattern = null;
        string root;
        string prefix;
        var files = new List<Item>();
        var dirs = new List<string>();

        if (name.IndexOfAny(['*', '?']) >= 0)
        {
            topLevelPattern = GlobToRegex(name);
            root = src.GetParent(srcPath);
            prefix = "";
        }
        else
        {
            var stat = await src.StatAsync(srcPath, ct).ConfigureAwait(false)
                       ?? throw new FileNotFoundException($"The source '{srcPath}' was not found on {src.DisplayName}.");
            if (!stat.IsDirectory)
            {
                result.SourceIsSingleFile = true;
                files.Add(new Item(stat, stat.Name));
                root = src.GetParent(srcPath);
                prefix = "";
            }
            else
            {
                if (!o.Recursive && !o.SyncMode)
                    throw new InvalidOperationException(
                        "The source is a folder: set --recursive (Options tab) or use a wildcard such as folder/*.");
                root = srcPath;
                prefix = o.AsSubdir && !o.SyncMode ? stat.Name : "";
            }
        }

        if (!result.SourceIsSingleFile)
        {
            output($"INFO: Scanning {src.DisplayName} {srcPath} …");
            await WalkAsync(src, root, "", topLevelPattern, include, exclude, o.Recursive || o.SyncMode, files, dirs, ct)
                .ConfigureAwait(false);
            if (prefix.Length > 0)
            {
                files = files.Select(f => new Item(f.Entry, prefix + "/" + f.RelativePath)).ToList();
                dirs = dirs.Select(d => prefix + "/" + d).Prepend(prefix).ToList();
            }
        }
        else if (!Matches(files[0].Entry.Name, include, exclude))
        {
            files.Clear();
        }

        result.Total = files.Count;
        var totalBytes = files.Sum(f => f.Entry.Length);

        // ---- 2. Resolve the destination --------------------------------------------------------
        Func<Item, string> targetOf = item => JoinRelative(dst, dstPath, item.RelativePath);

        if (result.SourceIsSingleFile)
        {
            var destStat = await dst.StatAsync(dstPath, ct).ConfigureAwait(false);
            var destIsDir = destStat?.IsDirectory == true || dst.EndsWithSeparator(dstPath);
            var single = destIsDir ? dst.Combine(dstPath, name) : dstPath;
            targetOf = _ => single;
            result.DestinationSelection = single;
        }
        else
        {
            result.DestinationSelection = prefix.Length > 0 ? JoinRelative(dst, dstPath, prefix) : dstPath;
            if (!o.DryRun)
            {
                await dst.CreateDirectoryAsync(dstPath, ct).ConfigureAwait(false);
                foreach (var d in dirs) await dst.CreateDirectoryAsync(JoinRelative(dst, dstPath, d), ct).ConfigureAwait(false);
            }
        }

        output($"INFO: {result.Total} file(s), {FormatBytes(totalBytes)} to process: {src.DisplayName} → {dst.DisplayName}");

        // ---- 3. Transfer ----------------------------------------------------------------------
        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long bytesAtLastReport = 0;
        var processedBytes = 0L;

        void Report(bool force)
        {
            var now = sw.Elapsed;
            if (!force && now - lastReport < TimeSpan.FromSeconds(1)) return;
            var seconds = Math.Max((now - lastReport).TotalSeconds, 0.001);
            var mbps = (result.Bytes - bytesAtLastReport) * 8 / 1_000_000.0 / seconds;
            lastReport = now;
            bytesAtLastReport = result.Bytes;
            var processed = result.Done + result.Failed + result.Skipped;
            var pct = totalBytes > 0 ? 100.0 * processedBytes / totalBytes
                : result.Total > 0 ? 100.0 * processed / result.Total : 100.0;
            output(string.Create(CultureInfo.InvariantCulture,
                $"{Math.Min(pct, 100):0.0} %, {result.Done} Done, {result.Failed} Failed, {result.Total - processed} Pending, {result.Skipped} Skipped, {result.Total} Total, 2-sec Throughput (Mb/s): {mbps:0.####}"));
        }

        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in files)
        {
            ct.ThrowIfCancellationRequested();
            var target = targetOf(item);
            sourcePaths.Add(item.RelativePath);
            try
            {
                var existing = await dst.StatAsync(target, ct).ConfigureAwait(false);
                if (existing?.IsDirectory == true)
                    throw new IOException($"'{target}' is a directory at the destination.");
                if (ShouldSkip(item.Entry, existing, o))
                {
                    result.Skipped++;
                    processedBytes += item.Entry.Length;
                    Report(false);
                    continue;
                }

                if (o.DryRun)
                {
                    output($"DRYRUN: copy {item.Entry.Path} → {target}");
                    result.Done++;
                    processedBytes += item.Entry.Length;
                    continue;
                }

                if (result.SourceIsSingleFile) await dst.CreateDirectoryAsync(dst.GetParent(target), ct).ConfigureAwait(false);

                var before = result.Bytes;
                void OnProgress(long fileBytes)
                {
                    result.Bytes = before + fileBytes;
                    Report(false);
                }

                if (dst.IsLocal)
                {
                    await using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                    await src.ReadIntoAsync(item.Entry.Path, fs, OnProgress, ct).ConfigureAwait(false);
                }
                else
                {
                    await using var stream = src.OpenRead(item.Entry.Path);
                    await dst.WriteFromAsync(target, stream, OnProgress, ct).ConfigureAwait(false);
                }

                result.Bytes = before + item.Entry.Length;
                await dst.SetLastWriteTimeUtcAsync(target, item.Entry.LastWriteUtc, ct).ConfigureAwait(false);
                result.Done++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Failed++;
                output($"ERROR: failed to copy {item.Entry.Path} → {target}: {ex.Message}");
            }

            processedBytes += item.Entry.Length;
            Report(false);
        }

        // ---- 4. Sync: delete extra destination files -----------------------------------------------
        if (o.SyncMode && o.DeleteDestination && !result.SourceIsSingleFile
            && await dst.StatAsync(dstPath, ct).ConfigureAwait(false) is { IsDirectory: true })
        {
            var destFiles = new List<Item>();
            await WalkAsync(dst, dstPath, "", null, [], [], true, destFiles, [], ct).ConfigureAwait(false);
            foreach (var extra in destFiles.Where(f => !sourcePaths.Contains(f.RelativePath)))
            {
                ct.ThrowIfCancellationRequested();
                if (o.DryRun)
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

        Report(true);
        output($"Transferred {FormatBytes(result.Bytes)} in {sw.Elapsed:hh\\:mm\\:ss}" + (result.Deleted > 0 ? $", deleted {result.Deleted} file(s)" : ""));
        return result;
    }

    private static bool ShouldSkip(FileEntry source, FileEntry? existing, TreeCopyOptions o)
    {
        if (existing == null) return false;
        if (o.SyncMode)
            return existing.Length == source.Length && existing.LastWriteUtc >= source.LastWriteUtc - TimeTolerance;
        return o.Overwrite switch
        {
            OverwriteMode.Never => true,
            OverwriteMode.IfSourceNewer => existing.LastWriteUtc >= source.LastWriteUtc - TimeTolerance,
            _ => false,
        };
    }

    private static async Task WalkAsync(IFileTree tree, string dir, string relative, Regex? topLevelPattern,
        List<Regex> include, List<Regex> exclude, bool recursive, List<Item> files, List<string> dirs, CancellationToken ct)
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
                files.Add(new Item(entry, rel));
            }
        }
    }

    private static bool Matches(string name, List<Regex> include, List<Regex> exclude) =>
        (include.Count == 0 || include.Any(r => r.IsMatch(name))) && !exclude.Any(r => r.IsMatch(name));

    /// <summary>Joins a '/'-separated relative path onto a destination root of the given tree.</summary>
    private static string JoinRelative(IFileTree tree, string root, string relative)
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
