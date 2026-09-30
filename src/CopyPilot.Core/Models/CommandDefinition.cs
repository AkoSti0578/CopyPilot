namespace CopyPilot.Core.Models;

/// <summary>Which positional arguments an azcopy command takes.</summary>
public enum PositionalKind
{
    None,
    /// <summary>Source and destination (copy, sync).</summary>
    SourceAndDestination,
    /// <summary>A single resource location (remove, list, make, set-properties, bench).</summary>
    Target,
    /// <summary>A job id (jobs show / resume / remove).</summary>
    JobId,
}

public sealed record CommandDefinition(
    string Id,
    IReadOnlyList<string> Verbs,
    string Title,
    string Description,
    PositionalKind Positionals,
    string TargetLabel = "Target")
{
    public override string ToString() => Title;
}

public static class CommandCatalog
{
    public const string Copy = "copy";
    public const string Sync = "sync";
    public const string Remove = "remove";
    public const string List = "list";
    public const string Make = "make";
    public const string SetProperties = "set-properties";
    public const string Bench = "bench";
    public const string Login = "login";
    public const string LoginStatus = "login status";
    public const string Logout = "logout";
    public const string JobsList = "jobs list";
    public const string JobsShow = "jobs show";
    public const string JobsResume = "jobs resume";
    public const string JobsRemove = "jobs remove";
    public const string JobsClean = "jobs clean";
    public const string Env = "env";

    public static IReadOnlyList<CommandDefinition> All { get; } =
    [
        new(Copy, ["copy"], "Copy",
            "Copies files, folders, containers, shares, buckets or whole accounts from a source to a destination.",
            PositionalKind.SourceAndDestination),
        new(Sync, ["sync"], "Sync",
            "Replicates the source to the destination, only transferring what changed (by last-modified time or hash).",
            PositionalKind.SourceAndDestination),
        new(Remove, ["remove"], "Remove",
            "Deletes blobs, files or directories from Azure Storage.",
            PositionalKind.Target, "Resource to remove"),
        new(List, ["list"], "List",
            "Lists the entities in a container, share, file system or directory.",
            PositionalKind.Target, "Resource to list"),
        new(Make, ["make"], "Make",
            "Creates a container, file share or file system.",
            PositionalKind.Target, "Resource to create"),
        new(SetProperties, ["set-properties"], "Set properties",
            "Changes access tier, metadata or blob index tags of blobs.",
            PositionalKind.Target, "Resource to update"),
        new(Bench, ["bench"], "Benchmark",
            "Runs a performance benchmark by uploading or downloading test data to/from the target.",
            PositionalKind.Target, "Benchmark target"),
        new(Login, ["login"], "Login",
            "Signs in to Microsoft Entra ID (device code, service principal, managed identity, Azure CLI, PowerShell, workload identity).",
            PositionalKind.None),
        new(LoginStatus, ["login", "status"], "Login status",
            "Shows whether you are currently signed in.",
            PositionalKind.None),
        new(Logout, ["logout"], "Logout",
            "Signs out and removes cached credentials.",
            PositionalKind.None),
        new(JobsList, ["jobs", "list"], "Jobs: list",
            "Lists all existing jobs.",
            PositionalKind.None),
        new(JobsShow, ["jobs", "show"], "Jobs: show",
            "Shows detailed information for a job.",
            PositionalKind.JobId),
        new(JobsResume, ["jobs", "resume"], "Jobs: resume",
            "Resumes a failed or cancelled job.",
            PositionalKind.JobId),
        new(JobsRemove, ["jobs", "remove"], "Jobs: remove",
            "Removes the plan and log files of a job.",
            PositionalKind.JobId),
        new(JobsClean, ["jobs", "clean"], "Jobs: clean",
            "Removes plan and log files of all (or filtered) jobs.",
            PositionalKind.None),
        new(Env, ["env"], "Environment",
            "Shows the environment variables that configure azcopy.",
            PositionalKind.None),
    ];

    public static CommandDefinition Get(string id) =>
        All.First(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
}
