namespace CopyPilot.Core.Models;

/// <summary>The kind of editor an option needs and how its value is rendered on the command line.</summary>
public enum OptionKind
{
    /// <summary>Boolean switch. Unset = azcopy default, true = <c>--name</c>, false = <c>--name=false</c>.</summary>
    Flag,
    Text,
    Number,
    Choice,
    FilePath,
    FolderPath,
    DateTime,
}

/// <summary>
/// Describes a single azcopy command-line flag or environment variable.
/// </summary>
/// <param name="Name">Flag name without leading dashes, or environment variable name.</param>
/// <param name="Kind">Editor / value kind.</param>
/// <param name="Category">Group shown in the UI.</param>
/// <param name="Description">Help text shown in the UI.</param>
/// <param name="Commands">Command ids the option applies to. <c>*</c> means all commands.</param>
/// <param name="Default">azcopy's default value (informational only).</param>
/// <param name="Choices">Suggested values for <see cref="OptionKind.Choice"/>.</param>
/// <param name="Sensitive">Value is a secret: masked in previews and never saved to profiles.</param>
/// <param name="CopyPilotOnly">A CopyPilot setting (e.g. for SFTP) that is never passed to azcopy.</param>
public sealed record OptionDefinition(
    string Name,
    OptionKind Kind,
    string Category,
    string Description,
    IReadOnlyList<string> Commands,
    string? Default = null,
    IReadOnlyList<string>? Choices = null,
    bool Sensitive = false,
    bool CopyPilotOnly = false)
{
    public bool AppliesTo(string commandId) =>
        Commands.Contains("*") || Commands.Contains(commandId, StringComparer.OrdinalIgnoreCase);

    public string CommandLineName => CopyPilotOnly ? Name : "--" + Name;
}
