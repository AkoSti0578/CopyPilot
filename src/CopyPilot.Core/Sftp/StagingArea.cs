using System.Globalization;

namespace CopyPilot.Core.Sftp;

/// <summary>
/// A per-job staging folder for SFTP ↔ cloud transfers. Files are removed with a permanent delete
/// (File.Delete never uses the Recycle Bin – the equivalent of Shift+Delete) as soon as they are transferred,
/// and the folder itself is removed when the job ends.
/// </summary>
public sealed class StagingArea : IDisposable
{
    /// <summary>More files than this per azcopy run make the command line / scan slow; flush earlier.</summary>
    public const int MaxFilesPerBatch = 1000;

    /// <summary>azcopy downloads to ".azDownload-&lt;job&gt;-name" and renames when complete (when this variable is true).</summary>
    public const string TempPathVariable = "AZCOPY_DOWNLOAD_TO_TEMP_PATH";

    public const string AzCopyTempPrefix = ".azDownload-";

    private readonly Action<string> _output;

    public StagingArea(string root, Action<string> output)
    {
        _output = output;
        Folder = Path.Combine(root, "job-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Folder);
        output($"INFO: Staging folder: {Folder} (files are permanently deleted as soon as they are transferred)");
    }

    public string Folder { get; }
    public long CurrentBytes { get; private set; }
    public long PeakBytes { get; private set; }

    public static string Describe(long limitBytes) =>
        limitBytes <= 0 ? "one file at a time" : TreeCopier.FormatBytes(limitBytes);

    /// <summary>Records a file that was added to the staging folder.</summary>
    public void Added(long bytes)
    {
        CurrentBytes += bytes;
        PeakBytes = Math.Max(PeakBytes, CurrentBytes);
    }

    /// <summary>Records the size of the staging folder as measured by a scan.</summary>
    public void Observe(long bytes)
    {
        CurrentBytes = bytes;
        PeakBytes = Math.Max(PeakBytes, bytes);
    }

    /// <summary>Permanently deletes a staged file (bypassing the Recycle Bin).</summary>
    public void Delete(string file, bool pruneEmptyFolders = false)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return;
            var length = info.Length;
            if (info.IsReadOnly) info.IsReadOnly = false;
            info.Delete();
            CurrentBytes = Math.Max(0, CurrentBytes - length);
            if (pruneEmptyFolders) PruneEmptyParents(info.DirectoryName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _output($"WARNING: could not delete staged file {file}: {ex.Message}");
        }
    }

    private void PruneEmptyParents(string? dir)
    {
        var root = Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar);
        while (dir != null)
        {
            var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Length <= root.Length || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.EnumerateFileSystemEntries(full).Any()) return;
            Directory.Delete(full);
            dir = Path.GetDirectoryName(full);
        }
    }

    /// <summary>Removes the staging folder and anything left in it (e.g. after cancelling).</summary>
    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var f in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _output($"WARNING: could not delete the staging folder {Folder}: {ex.Message}");
        }
    }
}

/// <summary>Finds files azcopy has finished downloading into the staging folder.</summary>
public sealed class StagingWatcher(string folder)
{
    private readonly Dictionary<string, (long Length, DateTime Modified)> _seen = new(StringComparer.Ordinal);

    /// <summary>Total size of everything in the staging folder at the last scan, including partial downloads.</summary>
    public long StagedBytes { get; private set; }

    /// <summary>
    /// Returns completed files. With <paramref name="requireStable"/>, a file must be unchanged since the previous
    /// scan and not be open in azcopy, so it is never picked up while azcopy is still finishing it.
    /// </summary>
    public List<FileEntry> Scan(bool requireStable)
    {
        var ready = new List<FileEntry>();
        long total = 0;
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ready;
        }

        foreach (var f in files)
        {
            long length;
            DateTime modified;
            try
            {
                f.Refresh();
                if (!f.Exists) continue;
                length = f.Length;
                modified = f.LastWriteTimeUtc;
            }
            catch (IOException)
            {
                continue;
            }

            total += length;
            if (f.Name.StartsWith(StagingArea.AzCopyTempPrefix, StringComparison.Ordinal)) continue;

            var stable = _seen.TryGetValue(f.FullName, out var prev) && prev.Length == length && prev.Modified == modified;
            _seen[f.FullName] = (length, modified);
            if (requireStable && (!stable || !CanOpenExclusively(f.FullName))) continue;
            ready.Add(new FileEntry(f.FullName, f.Name, false, length, modified));
        }

        StagedBytes = total;
        return ready.OrderBy(r => r.Path, StringComparer.Ordinal).ToList();
    }

    public void Forget(string path) => _seen.Remove(path);

    public long StagedBytesAfterDelete(long bytes) => StagedBytes = Math.Max(0, StagedBytes - bytes);

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
