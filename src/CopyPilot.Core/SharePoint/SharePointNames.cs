namespace CopyPilot.Core.SharePoint;

/// <summary>SharePoint Online / OneDrive naming rules, checked before upload so failures are clear.</summary>
public static class SharePointNames
{
    /// <summary>Maximum length of the decoded path (site + library + folders + file name).</summary>
    public const int MaxPathLength = 400;

    private static readonly char[] InvalidChars = ['"', '*', ':', '<', '>', '?', '/', '\\', '|'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lock", "CON", "PRN", "AUX", "NUL", "desktop.ini",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Returns why <paramref name="relativePath"/> cannot be stored in SharePoint, or null when it is fine.</summary>
    /// <param name="prefixLength">Length of the site path and library that precede the relative path.</param>
    public static string? Problem(string relativePath, int prefixLength = 0)
    {
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.IndexOfAny(InvalidChars) >= 0)
                return $"'{segment}' contains a character SharePoint does not allow (\" * : < > ? / \\ |).";
            if (ReservedNames.Contains(segment))
                return $"'{segment}' is a reserved name in SharePoint.";
            if (segment.StartsWith("~$", StringComparison.Ordinal))
                return $"'{segment}' starts with ~$ (Office lock file), which SharePoint does not allow.";
            if (segment.Contains("_vti_", StringComparison.OrdinalIgnoreCase))
                return $"'{segment}' contains _vti_, which SharePoint does not allow.";
            if (segment != segment.Trim())
                return $"'{segment}' starts or ends with a space, which SharePoint does not allow.";
        }

        if (prefixLength + relativePath.Length + 1 > MaxPathLength)
            return $"the path is longer than SharePoint's limit of {MaxPathLength} characters.";
        return null;
    }
}
