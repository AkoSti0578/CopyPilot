using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CopyPilot.Core.Services;

/// <summary>Finds the azcopy executable and can download the latest release.</summary>
public static partial class AzCopyLocator
{
    public static string ExeName => OperatingSystem.IsWindows() ? "azcopy.exe" : "azcopy";

    /// <summary>Folder where "Update azcopy" stores the downloaded version.</summary>
    public static string UserAzCopyFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CopyPilot", "azcopy");

    /// <summary>Folder where the bundled azcopy lives next to CopyPilot.</summary>
    public static string BundledAzCopyFolder => Path.Combine(AppContext.BaseDirectory, "azcopy");

    /// <summary>
    /// Candidates in priority order: explicit override, user-downloaded update, bundled copy, PATH.
    /// </summary>
    public static IEnumerable<string> Candidates(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) yield return overridePath.Trim();
        yield return Path.Combine(UserAzCopyFolder, ExeName);
        yield return Path.Combine(BundledAzCopyFolder, ExeName);
        yield return Path.Combine(AppContext.BaseDirectory, ExeName);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(dir, ExeName);
        }
    }

    public static string? Find(string? overridePath) => Candidates(overridePath).FirstOrDefault(File.Exists);

    [GeneratedRegex(@"azcopy version\s+(?<v>\d+\.\d+\.\d+\S*)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    /// <summary>Extracts "10.x.y" from the output of <c>azcopy --version</c>.</summary>
    public static string? ParseVersion(string output)
    {
        var m = VersionRegex().Match(output);
        return m.Success ? m.Groups["v"].Value : null;
    }

    public static async Task<string?> GetVersionAsync(string exe)
    {
        try
        {
            var output = await AzCopyRunner.CaptureAsync(exe, ["--version"], TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return ParseVersion(output) ?? output.Split('\n').FirstOrDefault()?.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Microsoft's permalink to the latest azcopy v10 release for this machine.</summary>
    public static string LatestDownloadUrl => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "https://aka.ms/downloadazcopy-v10-windows-arm64",
        Architecture.X86 => "https://aka.ms/downloadazcopy-v10-windows-32bit",
        _ => "https://aka.ms/downloadazcopy-v10-windows",
    };

    /// <summary>Downloads the latest azcopy into <see cref="UserAzCopyFolder"/> and returns the exe path.</summary>
    public static async Task<string> DownloadLatestAsync(IProgress<string>? progress, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CopyPilot/1.0");
        progress?.Report($"Downloading {LatestDownloadUrl} …");
        var tempZip = Path.Combine(Path.GetTempPath(), $"azcopy-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var response = await http.GetStreamAsync(LatestDownloadUrl, ct).ConfigureAwait(false))
            await using (var file = File.Create(tempZip))
            {
                await response.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            progress?.Report("Extracting …");
            using var zip = ZipFile.OpenRead(tempZip);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("azcopy.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("azcopy.exe was not found in the downloaded archive.");
            Directory.CreateDirectory(UserAzCopyFolder);
            var target = Path.Combine(UserAzCopyFolder, "azcopy.exe");
            var staging = target + ".new";
            entry.ExtractToFile(staging, overwrite: true);
            File.Move(staging, target, overwrite: true);
            progress?.Report("Done.");
            return target;
        }
        finally
        {
            try { File.Delete(tempZip); } catch (IOException) { }
        }
    }
}
