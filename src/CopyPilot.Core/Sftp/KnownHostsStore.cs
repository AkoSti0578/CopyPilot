namespace CopyPilot.Core.Sftp;

public enum KnownHostResult
{
    Unknown,
    Match,
    Mismatch,
}

/// <summary>
/// CopyPilot's own known-hosts file: one "host[:port] SHA256:fingerprint" per line.
/// </summary>
public sealed class KnownHostsStore(string file)
{
    private static readonly object Gate = new();

    public KnownHostResult Check(string endpoint, string fingerprint, out string? stored)
    {
        stored = null;
        lock (Gate)
        {
            if (!File.Exists(file)) return KnownHostResult.Unknown;
            foreach (var line in File.ReadAllLines(file))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[0].StartsWith('#')) continue;
                if (!string.Equals(parts[0], endpoint, StringComparison.OrdinalIgnoreCase)) continue;
                stored = parts[1];
                if (string.Equals(parts[1], fingerprint, StringComparison.Ordinal)) return KnownHostResult.Match;
            }
        }

        return stored == null ? KnownHostResult.Unknown : KnownHostResult.Mismatch;
    }

    public void Add(string endpoint, string fingerprint)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.AppendAllText(file, $"{endpoint} {fingerprint}{Environment.NewLine}");
        }
    }
}
