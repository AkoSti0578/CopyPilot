namespace CopyPilot.Core.Sftp;

/// <summary>A file or directory in a <see cref="IFileTree"/>.</summary>
public sealed record FileEntry(string Path, string Name, bool IsDirectory, long Length, DateTime LastWriteUtc);

/// <summary>
/// Minimal file system abstraction so the same copy engine works for local disks and SFTP servers.
/// </summary>
public interface IFileTree : IDisposable
{
    /// <summary>Human-readable name used in log lines, e.g. "local" or "sftp://host".</summary>
    string DisplayName { get; }

    bool IsLocal { get; }

    string Combine(string directory, string name);
    string GetName(string path);
    string GetParent(string path);

    /// <summary>True when the path explicitly denotes a directory (trailing separator).</summary>
    bool EndsWithSeparator(string path);

    Task<FileEntry?> StatAsync(string path, CancellationToken ct);
    Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken ct);
    Task CreateDirectoryAsync(string path, CancellationToken ct);
    Task SetLastWriteTimeUtcAsync(string path, DateTime utc, CancellationToken ct);
    Task DeleteFileAsync(string path, CancellationToken ct);

    Stream OpenRead(string path);

    /// <summary>Downloads <paramref name="path"/> into <paramref name="destination"/>. Progress is cumulative bytes of this file.</summary>
    Task ReadIntoAsync(string path, Stream destination, Action<long> progress, CancellationToken ct);

    /// <summary>Creates/overwrites <paramref name="path"/> with the content of <paramref name="source"/>. Progress is cumulative bytes.</summary>
    Task WriteFromAsync(string path, Stream source, Action<long> progress, CancellationToken ct);
}

/// <summary>Synchronous IProgress (Progress&lt;T&gt; would post to the UI thread).</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

public sealed class LocalFileTree : IFileTree
{
    public string DisplayName => "local";
    public bool IsLocal => true;

    public string Combine(string directory, string name) => Path.Combine(directory, name);

    public string GetName(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    public string GetParent(string path) => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path;

    public bool EndsWithSeparator(string path) => path.EndsWith('/') || path.EndsWith('\\');

    public Task<FileEntry?> StatAsync(string path, CancellationToken ct)
    {
        FileEntry? result = null;
        if (Directory.Exists(path))
        {
            var d = new DirectoryInfo(path);
            result = new FileEntry(path, d.Name, true, 0, d.LastWriteTimeUtc);
        }
        else if (File.Exists(path))
        {
            var f = new FileInfo(path);
            result = new FileEntry(path, f.Name, false, f.Length, f.LastWriteTimeUtc);
        }

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken ct)
    {
        var list = new DirectoryInfo(directory).EnumerateFileSystemInfos()
            .Select(i => i is DirectoryInfo d
                ? new FileEntry(d.FullName, d.Name, true, 0, d.LastWriteTimeUtc)
                : new FileEntry(i.FullName, i.Name, false, ((FileInfo)i).Length, i.LastWriteTimeUtc))
            .ToList();
        return Task.FromResult<IReadOnlyList<FileEntry>>(list);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task SetLastWriteTimeUtcAsync(string path, DateTime utc, CancellationToken ct)
    {
        File.SetLastWriteTimeUtc(path, utc);
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string path, CancellationToken ct)
    {
        File.Delete(path);
        return Task.CompletedTask;
    }

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);

    public async Task ReadIntoAsync(string path, Stream destination, Action<long> progress, CancellationToken ct)
    {
        await using var source = OpenRead(path);
        await CopyWithProgressAsync(source, destination, progress, ct).ConfigureAwait(false);
    }

    public async Task WriteFromAsync(string path, Stream source, Action<long> progress, CancellationToken ct)
    {
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await CopyWithProgressAsync(source, target, progress, ct).ConfigureAwait(false);
    }

    internal static async Task CopyWithProgressAsync(Stream source, Stream target, Action<long> progress, CancellationToken ct)
    {
        var buffer = new byte[1 << 17];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
            progress(total);
        }
    }

    public void Dispose()
    {
    }
}
