using CopyPilot.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CopyPilot.Core.Sftp;

/// <summary>An SFTP server as an <see cref="IFileTree"/>, built on SSH.NET.</summary>
public sealed class SftpFileTree : IFileTree
{
    private readonly SftpClient _client;
    private readonly HashSet<string> _knownDirectories = new(StringComparer.Ordinal);

    private SftpFileTree(SftpClient client, string displayName)
    {
        _client = client;
        DisplayName = displayName;
    }

    public string DisplayName { get; }
    public bool IsLocal => false;

    /// <summary>
    /// Connects and authenticates. The host key is checked against <see cref="LocationSpec.HostKeyFingerprint"/>
    /// when set, otherwise against the known-hosts file (trust on first use).
    /// </summary>
    public static async Task<SftpFileTree> ConnectAsync(
        LocationSpec spec, string? knownHostsFile, TimeSpan timeout, Action<string> log, CancellationToken ct)
    {
        var host = spec.Host.Trim();
        var user = spec.Username.Trim();
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(spec.PrivateKeyPath))
        {
            var key = string.IsNullOrEmpty(spec.PrivateKeyPassphrase)
                ? new PrivateKeyFile(spec.PrivateKeyPath.Trim())
                : new PrivateKeyFile(spec.PrivateKeyPath.Trim(), spec.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }

        if (!string.IsNullOrEmpty(spec.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(user, spec.Password));
            var kbd = new KeyboardInteractiveAuthenticationMethod(user);
            kbd.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = spec.Password;
            };
            methods.Add(kbd);
        }

        var info = new ConnectionInfo(host, spec.Port, user, methods.ToArray()) { Timeout = timeout };
        var client = new SftpClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };

        var endpoint = spec.Port == 22 ? host : $"{host}:{spec.Port}";
        string? hostKeyProblem = null;
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = "SHA256:" + e.FingerPrintSHA256;
            var expected = NormalizeFingerprint(spec.HostKeyFingerprint);
            if (expected.Length > 0)
            {
                e.CanTrust = string.Equals(expected, fingerprint, StringComparison.Ordinal);
                if (!e.CanTrust)
                    hostKeyProblem = $"Host key of {endpoint} is {fingerprint}, but {expected} was expected.";
                return;
            }

            if (knownHostsFile == null)
            {
                log($"INFO: SFTP host key of {endpoint}: {e.HostKeyName} {fingerprint} (not verified)");
                e.CanTrust = true;
                return;
            }

            var store = new KnownHostsStore(knownHostsFile);
            switch (store.Check(endpoint, fingerprint, out var stored))
            {
                case KnownHostResult.Match:
                    e.CanTrust = true;
                    break;
                case KnownHostResult.Unknown:
                    store.Add(endpoint, fingerprint);
                    log($"INFO: Trusting new SFTP host {endpoint} ({e.HostKeyName} {fingerprint}); saved to {knownHostsFile}");
                    e.CanTrust = true;
                    break;
                default:
                    hostKeyProblem =
                        $"WARNING: THE HOST KEY OF {endpoint} HAS CHANGED (now {fingerprint}, known {stored}). " +
                        $"This could be an attack. If the change is expected, enter the new fingerprint in the location or remove the line from {knownHostsFile}.";
                    e.CanTrust = false;
                    break;
            }
        };

        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (hostKeyProblem != null && ex is SshConnectionException or SshException)
        {
            client.Dispose();
            throw new InvalidOperationException(hostKeyProblem, ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new SftpFileTree(client, $"sftp://{endpoint}");
    }

    public static string NormalizeFingerprint(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return "";
        if (v.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) v = v[7..];
        return "SHA256:" + v.TrimEnd('=');
    }

    public string Combine(string directory, string name)
    {
        if (directory is "" or ".") return name;
        return directory.EndsWith('/') ? directory + name : directory + "/" + name;
    }

    public string GetName(string path)
    {
        var p = path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i < 0 ? p : p[(i + 1)..];
    }

    public string GetParent(string path)
    {
        var p = path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i < 0 ? "." : i == 0 ? "/" : p[..i];
    }

    public bool EndsWithSeparator(string path) => path.EndsWith('/');

    public async Task<FileEntry?> StatAsync(string path, CancellationToken ct)
    {
        try
        {
            var a = await _client.GetAttributesAsync(path, ct).ConfigureAwait(false);
            return new FileEntry(path, GetName(path), a.IsDirectory, a.IsDirectory ? 0 : a.Size, a.LastWriteTimeUtc);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken ct)
    {
        var list = new List<FileEntry>();
        await foreach (var f in _client.ListDirectoryAsync(directory, ct).ConfigureAwait(false))
        {
            if (f.Name is "." or "..") continue;
            if (!f.IsDirectory && !f.IsRegularFile) continue; // skip symlinks to nowhere, sockets, devices
            list.Add(new FileEntry(Combine(directory, f.Name), f.Name, f.IsDirectory, f.IsDirectory ? 0 : f.Length, f.LastWriteTimeUtc));
        }

        return list;
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        if (path is "" or "." or "/" || _knownDirectories.Contains(path)) return;
        var parent = GetParent(path);
        if (parent != path) await CreateDirectoryAsync(parent, ct).ConfigureAwait(false);
        var existing = await StatAsync(path, ct).ConfigureAwait(false);
        if (existing == null)
            await _client.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
        else if (!existing.IsDirectory)
            throw new IOException($"'{path}' exists on the SFTP server but is not a directory.");
        _knownDirectories.Add(path);
    }

    public Task SetLastWriteTimeUtcAsync(string path, DateTime utc, CancellationToken ct) =>
        Task.Run(() => _client.SetLastWriteTimeUtc(path, utc), ct);

    public Task DeleteFileAsync(string path, CancellationToken ct) => _client.DeleteFileAsync(path, ct);

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => Task.Run<Stream>(() => _client.OpenRead(path), ct);

    public Task ReadIntoAsync(string path, Stream destination, Action<long> progress, CancellationToken ct) =>
        _client.DownloadFileAsync(path, destination,
            new InlineProgress<DownloadFileProgressReport>(r => progress((long)r.TotalBytesDownloaded)), ct);

    public Task WriteFromAsync(string path, Stream source, long length, DateTime lastWriteUtc, Action<long> progress, CancellationToken ct) =>
        _client.UploadFileAsync(source, path, true,
            new InlineProgress<UploadFileProgressReport>(r => progress((long)r.TotalBytesUploaded)), ct);

    public void Dispose()
    {
        try
        {
            if (_client.IsConnected) _client.Disconnect();
        }
        catch (Exception)
        {
            // best-effort
        }

        _client.Dispose();
    }
}
