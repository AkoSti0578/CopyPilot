using CopyPilot.Core.Models;
using CopyPilot.Core.Services;
using CopyPilot.Core.Sftp;
using Xunit;

namespace CopyPilot.Core.Tests;

/// <summary>Creates a temporary folder tree and removes it afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cp-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Write(string relative, string content, DateTime? lastWriteUtc = null)
    {
        var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (lastWriteUtc != null) File.SetLastWriteTimeUtc(full, lastWriteUtc.Value);
        return full;
    }

    public string[] Files() => Directory.GetFiles(Path, "*", SearchOption.AllDirectories)
        .Select(f => System.IO.Path.GetRelativePath(Path, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public class TreeCopierLocalTests
{
    private static async Task<(TreeCopyResult Result, List<string> Log)> Copy(string src, string dst, TreeCopyOptions o)
    {
        var log = new List<string>();
        var r = await new TreeCopier(log.Add).CopyAsync(new LocalFileTree(), src, new LocalFileTree(), dst, o, CancellationToken.None);
        return (r, log);
    }

    private static void Seed(TempDir d)
    {
        d.Write("data/a.txt", "a");
        d.Write("data/b.csv", "bb");
        d.Write("data/sub/c.txt", "ccc");
    }

    [Fact]
    public async Task Folder_as_subdir_recursive()
    {
        using var s = new TempDir();
        using var t = new TempDir();
        Seed(s);
        var (r, log) = await Copy(Path.Combine(s.Path, "data"), t.Path, new TreeCopyOptions { Recursive = true });
        Assert.Equal(3, r.Done);
        Assert.Equal(["data/a.txt", "data/b.csv", "data/sub/c.txt"], t.Files());
        Assert.Contains(log, l => l.StartsWith("100.0 %, 3 Done"));
        Assert.Equal("Completed", r.Status);
    }

    [Fact]
    public async Task Folder_contents_with_patterns()
    {
        using var s = new TempDir();
        using var t = new TempDir();
        Seed(s);
        var (r, _) = await Copy(Path.Combine(s.Path, "data"), t.Path,
            new TreeCopyOptions { Recursive = true, AsSubdir = false, IncludePatterns = ["*.txt"] });
        Assert.Equal(2, r.Total);
        Assert.Equal(["a.txt", "sub/c.txt"], t.Files());
    }

    [Fact]
    public async Task Folder_without_recursive_is_an_error_but_wildcard_works()
    {
        using var s = new TempDir();
        using var t = new TempDir();
        Seed(s);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Copy(Path.Combine(s.Path, "data"), t.Path, new TreeCopyOptions()));
        var (r, _) = await Copy(Path.Combine(s.Path, "data", "*.csv"), t.Path, new TreeCopyOptions());
        Assert.Equal(1, r.Done);
        Assert.Equal(["b.csv"], t.Files());
    }

    [Fact]
    public async Task Single_file_to_file_path_or_folder()
    {
        using var s = new TempDir();
        using var t = new TempDir();
        var file = s.Write("x.txt", "x");
        await Copy(file, Path.Combine(t.Path, "renamed.txt"), new TreeCopyOptions());
        Directory.CreateDirectory(Path.Combine(t.Path, "dir"));
        await Copy(file, Path.Combine(t.Path, "dir"), new TreeCopyOptions());
        Assert.Equal(["dir/x.txt", "renamed.txt"], t.Files());
    }

    [Fact]
    public async Task Overwrite_modes_and_sync_with_delete()
    {
        using var s = new TempDir();
        using var t = new TempDir();
        var old = DateTime.UtcNow.AddHours(-2);
        s.Write("a.txt", "new-a");
        s.Write("b.txt", "b", old);
        t.Write("a.txt", "old", old);
        t.Write("b.txt", "b", old);
        t.Write("extra.txt", "x");

        var (never, _) = await Copy(Path.Combine(s.Path, "*"), t.Path, new TreeCopyOptions { Overwrite = OverwriteMode.Never });
        Assert.Equal(2, never.Skipped);

        var (sync, _) = await Copy(s.Path, t.Path, new TreeCopyOptions { SyncMode = true, DeleteDestination = true });
        Assert.Equal(1, sync.Done);     // a.txt is newer
        Assert.Equal(1, sync.Skipped);  // b.txt unchanged
        Assert.Equal(1, sync.Deleted);  // extra.txt
        Assert.Equal("new-a", File.ReadAllText(Path.Combine(t.Path, "a.txt")));
        Assert.Equal(["a.txt", "b.txt"], t.Files());
    }

    [Fact]
    public async Task Dry_run_changes_nothing()
    {
        using var s = new TempDir();
        using var t = new TempDir();
        Seed(s);
        var (r, log) = await Copy(Path.Combine(s.Path, "data"), t.Path, new TreeCopyOptions { Recursive = true, DryRun = true });
        Assert.Equal(3, r.Done);
        Assert.Empty(t.Files());
        Assert.Equal(3, log.Count(l => l.StartsWith("DRYRUN:")));
    }
}

public class TransferPlannerTests
{
    private static readonly LocationSpec Sftp = new()
    {
        Kind = LocationKind.Sftp, Host = "sftp.example.com", Port = 2222, Username = "arnold", Password = "pw", Path = "/upload/in",
    };

    private static readonly LocationSpec Blob = new()
    {
        Kind = LocationKind.AzureBlob, Account = "acct", Container = "c", Sas = "sv=1&sig=SECRET",
    };

    [Fact]
    public void Plain_azcopy_request_is_unchanged()
    {
        var plan = TransferPlanner.Plan(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy),
            Source = new LocationSpec { LocalPath = "C:\\x" },
            Destination = Blob,
        });
        Assert.NotNull(plan.AzCopy);
        Assert.False(plan.IsSftp);
        Assert.True(plan.CanExportScript);
    }

    [Fact]
    public void Local_to_sftp_is_direct_and_warns_about_unused_options()
    {
        var plan = TransferPlanner.Plan(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy),
            Source = new LocationSpec { LocalPath = "/tmp/x" },
            Destination = Sftp,
            Options = new Dictionary<string, string> { ["recursive"] = "true", ["block-size-mb"] = "8", ["overwrite"] = "ifSourceNewer" },
        });
        Assert.True(plan.IsValid, string.Join(", ", plan.Errors));
        Assert.Equal(SftpRoute.Direct, plan.Route);
        Assert.False(plan.NeedsAzCopy);
        Assert.True(plan.TreeOptions!.Recursive);
        Assert.Equal(OverwriteMode.IfSourceNewer, plan.TreeOptions.Overwrite);
        Assert.Contains(plan.Warnings, w => w.Contains("block-size-mb"));
        Assert.Contains("sftp://arnold@sftp.example.com:2222/upload/in", plan.ToDisplayString());
        Assert.DoesNotContain("pw", plan.ToDisplayString().Replace("upload", ""));
    }

    [Fact]
    public void Sftp_to_blob_stages_then_runs_the_users_command()
    {
        var plan = TransferPlanner.Plan(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy),
            Source = Sftp,
            Destination = Blob,
            Options = new Dictionary<string, string> { ["recursive"] = "true", ["block-blob-tier"] = "Cool", ["sftp-keep-staged-files"] = "true" },
        });
        Assert.True(plan.IsValid, string.Join(", ", plan.Errors));
        Assert.Equal(SftpRoute.SftpToCloud, plan.Route);
        Assert.True(plan.KeepStagedFiles);

        var leg = TransferPlanner.BuildAzCopyLeg(plan, new LocationSpec { LocalPath = "/stage/in" }, null);
        Assert.Equal(["copy", "/stage/in", "https://acct.blob.core.windows.net/c?sv=1&sig=SECRET", "--recursive", "--block-blob-tier=Cool"], leg.Arguments);
        Assert.DoesNotContain("SECRET", plan.ToDisplayString());
    }

    [Fact]
    public void Blob_to_sftp_sync_downloads_with_copy_and_uploads_changed_files()
    {
        var plan = TransferPlanner.Plan(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Sync),
            Source = Blob,
            Destination = Sftp,
            Options = new Dictionary<string, string> { ["delete-destination"] = "true", ["include-pattern"] = "*.csv" },
        });
        Assert.True(plan.IsValid, string.Join(", ", plan.Errors));
        Assert.Equal(SftpRoute.CloudToSftp, plan.Route);
        Assert.True(plan.TreeOptions!.SyncMode);
        Assert.True(plan.TreeOptions.DeleteDestination);

        var leg = TransferPlanner.BuildAzCopyLeg(plan, null, new LocationSpec { LocalPath = "/stage" });
        Assert.Equal("copy", leg.Arguments[0]);
        Assert.Contains("--recursive", leg.Arguments);
        Assert.Contains("--include-pattern=*.csv", leg.Arguments);
        Assert.Contains("--preserve-last-modified-time", leg.Arguments);
        Assert.DoesNotContain(leg.Arguments, a => a.StartsWith("--delete-destination"));
    }

    [Fact]
    public void Sftp_validation_and_unsupported_commands()
    {
        var plan = TransferPlanner.Plan(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy),
            Source = new LocationSpec { Kind = LocationKind.Sftp },
            Destination = new LocationSpec { LocalPath = "/tmp" },
        });
        Assert.Contains(plan.Errors, e => e.Contains("host"));
        Assert.Contains(plan.Errors, e => e.Contains("password"));

        var remove = TransferPlanner.Plan(new CommandRequest { Command = CommandCatalog.Get(CommandCatalog.Remove), Target = Sftp });
        Assert.Contains(remove.Errors, e => e.Contains("SFTP"));
    }

    [Fact]
    public void Profile_strips_sftp_secrets()
    {
        var json = new Profile { Source = Sftp.Clone() }.ToJson(includeSecrets: false);
        Assert.DoesNotContain("\"pw\"", json);
        Assert.Contains("sftp.example.com", json);
    }
}

/// <summary>
/// Integration tests against a real SFTP server. They run only when COPYPILOT_SFTP_TEST is set to
/// "host:port:user:password[:privateKeyPath:passphrase]".
/// </summary>
public class SftpIntegrationTests
{
    private static readonly string[]? Config = Environment.GetEnvironmentVariable("COPYPILOT_SFTP_TEST")?.Split(':');

    private static LocationSpec Server(string path, bool useKey = false) => new()
    {
        Kind = LocationKind.Sftp,
        Host = Config![0],
        Port = int.Parse(Config[1]),
        Username = Config[2],
        Password = useKey ? "" : Config[3],
        PrivateKeyPath = useKey ? Config[4] : "",
        PrivateKeyPassphrase = useKey ? Config[5] : "",
        Path = path,
    };

    private static async Task<int> Run(ExecutionPlan plan, List<string> log, string knownHosts)
    {
        Assert.True(plan.IsValid, string.Join(", ", plan.Errors));
        var runner = new TransferRunner(new AzCopyRunner()) { KnownHostsFile = knownHosts };
        return await runner.RunAsync(plan, null, new Dictionary<string, string>(), (l, _) => log.Add(l), CancellationToken.None);
    }

    [Fact]
    public async Task Round_trip_sync_and_host_key_checks()
    {
        if (Config == null) return;
        using var local = new TempDir();
        using var back = new TempDir();
        using var state = new TempDir();
        var knownHosts = Path.Combine(state.Path, "known_hosts");
        var remoteRoot = "copypilot-test-" + Guid.NewGuid().ToString("N")[..8];
        local.Write("data/a.txt", "hello");
        local.Write("data/deep/b.bin", new string('x', 300_000));
        local.Write("data/skip.log", "log");
        var copy = CommandCatalog.Get(CommandCatalog.Copy);
        var log = new List<string>();

        // 1. Upload local folder → SFTP (password auth, trust on first use).
        var up = TransferPlanner.Plan(new CommandRequest
        {
            Command = copy,
            Source = new LocationSpec { LocalPath = Path.Combine(local.Path, "data") },
            Destination = Server(remoteRoot),
            Options = new Dictionary<string, string> { ["recursive"] = "true", ["exclude-pattern"] = "*.log" },
        });
        Assert.Equal(0, await Run(up, log, knownHosts));
        Assert.Contains(log, l => l.Contains("Trusting new SFTP host"));
        Assert.Contains("Final Job Status: Completed", log);

        // 2. Download SFTP → local with key auth; host key now known.
        log.Clear();
        var down = TransferPlanner.Plan(new CommandRequest
        {
            Command = copy,
            Source = Server(remoteRoot + "/data", useKey: Config.Length > 4),
            Destination = new LocationSpec { LocalPath = back.Path },
            Options = new Dictionary<string, string> { ["recursive"] = "true" },
        });
        Assert.Equal(0, await Run(down, log, knownHosts));
        Assert.DoesNotContain(log, l => l.Contains("Trusting new SFTP host"));
        Assert.Equal(["data/a.txt", "data/deep/b.bin"], back.Files());
        Assert.Equal(300_000, new FileInfo(Path.Combine(back.Path, "data", "deep", "b.bin")).Length);

        // 3. Sync again: nothing changed → everything skipped; then SFTP → SFTP single file.
        log.Clear();
        var sync = TransferPlanner.Plan(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Sync),
            Source = new LocationSpec { LocalPath = Path.Combine(local.Path, "data") },
            Destination = Server(remoteRoot + "/data"),
            Options = new Dictionary<string, string> { ["exclude-pattern"] = "*.log" },
        });
        Assert.Equal(0, await Run(sync, log, knownHosts));
        Assert.Contains(log, l => l.StartsWith("100.0 %, 0 Done, 0 Failed, 0 Pending, 2 Skipped"));

        log.Clear();
        var s2s = TransferPlanner.Plan(new CommandRequest
        {
            Command = copy,
            Source = Server(remoteRoot + "/data/a.txt"),
            Destination = Server(remoteRoot + "/copy-of-a.txt"),
        });
        Assert.Equal(0, await Run(s2s, log, knownHosts));

        // 4. A wrong pinned fingerprint must refuse to connect.
        var bad = Server(remoteRoot);
        bad.HostKeyFingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var badPlan = TransferPlanner.Plan(new CommandRequest
        {
            Command = copy,
            Source = new LocationSpec { LocalPath = Path.Combine(local.Path, "data", "a.txt") },
            Destination = bad,
        });
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Run(badPlan, log, knownHosts));
        Assert.Contains("expected", ex.Message);
    }
}

/// <summary>
/// Staged SFTP ↔ cloud routes with a fake azcopy shell script (Linux/macOS only, needs COPYPILOT_SFTP_TEST).
/// The fake maps the "cloud" URL to the folder in FAKE_CLOUD_DIR.
/// </summary>
public class StagedRouteTests
{
    private static readonly string[]? Config = Environment.GetEnvironmentVariable("COPYPILOT_SFTP_TEST")?.Split(':');

    private static LocationSpec Server(string path) => new()
    {
        Kind = LocationKind.Sftp, Host = Config![0], Port = int.Parse(Config[1]), Username = Config[2], Password = Config[3], Path = path,
    };

    private static string FakeAzCopy(TempDir dir, string cloudDir)
    {
        // Records the arguments, then copies: URL → cloud dir, local → cloud dir or cloud dir → local.
        var script = Path.Combine(dir.Path, "azcopy");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            echo "ARGS: $*"
            src="$2"; dst="$3"
            case "$src" in https://*) src="{{cloudDir}}/*";; esac
            case "$dst" in https://*) dst="{{cloudDir}}";; esac
            mkdir -p "$dst"
            cp -rp $src "$dst"/ || exit 1
            echo "Final Job Status: Completed"
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    [Fact]
    public async Task Sftp_to_cloud_and_back()
    {
        if (Config == null || OperatingSystem.IsWindows()) return;
        using var tools = new TempDir();
        using var cloud = new TempDir();
        using var local = new TempDir();
        using var stage = new TempDir();
        var exe = FakeAzCopy(tools, cloud.Path);
        var remote = "copypilot-staged-" + Guid.NewGuid().ToString("N")[..8];
        var runner = new TransferRunner(new AzCopyRunner()) { KnownHostsFile = Path.Combine(tools.Path, "known_hosts") };
        var log = new List<string>();
        var url = new LocationSpec { Kind = LocationKind.CustomUrl, Url = "https://acct.blob.core.windows.net/c" };
        var staging = new Dictionary<string, string> { ["recursive"] = "true", ["sftp-staging-folder"] = stage.Path };

        // Seed the SFTP server.
        local.Write("in/one.txt", "1");
        local.Write("in/sub/two.txt", "22");
        var seed = TransferPlanner.Plan(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy),
            Source = new LocationSpec { LocalPath = Path.Combine(local.Path, "in") }, Destination = Server(remote),
            Options = new Dictionary<string, string> { ["recursive"] = "true" },
        });
        Assert.Equal(0, await runner.RunAsync(seed, null, new Dictionary<string, string>(), (l, _) => log.Add(l), CancellationToken.None));

        // SFTP → "cloud": staged download, then azcopy uploads <stage>/in.
        var toCloud = TransferPlanner.Plan(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy), Source = Server(remote + "/in"), Destination = url, Options = staging,
        });
        Assert.Equal(0, await runner.RunAsync(toCloud, exe, new Dictionary<string, string>(), (l, _) => log.Add(l), CancellationToken.None));
        Assert.Equal(["in/one.txt", "in/sub/two.txt"], cloud.Files());
        Assert.Contains(log, l => l.StartsWith("ARGS: copy ") && l.Contains("/in https://acct.blob.core.windows.net/c --recursive"));
        Assert.Empty(stage.Files()); // staging cleaned up

        // "cloud" → SFTP: azcopy downloads to staging, CopyPilot uploads.
        var toSftp = TransferPlanner.Plan(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy), Source = url, Destination = Server(remote + "/back"), Options = staging,
        });
        log.Clear();
        Assert.Equal(0, await runner.RunAsync(toSftp, exe, new Dictionary<string, string>(), (l, _) => log.Add(l), CancellationToken.None));
        Assert.Contains(log, l => l.Contains("--preserve-last-modified-time"));
        Assert.Contains(log, l => l.StartsWith("100.0 %, 2 Done"));

        // Verify by downloading what landed on the server.
        using var check = new TempDir();
        var verify = TransferPlanner.Plan(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy), Source = Server(remote + "/back/*"),
            Destination = new LocationSpec { LocalPath = check.Path }, Options = new Dictionary<string, string> { ["recursive"] = "true" },
        });
        Assert.Equal(0, await runner.RunAsync(verify, null, new Dictionary<string, string>(), (l, _) => log.Add(l), CancellationToken.None));
        Assert.Equal(["in/one.txt", "in/sub/two.txt"], check.Files());
    }
}
