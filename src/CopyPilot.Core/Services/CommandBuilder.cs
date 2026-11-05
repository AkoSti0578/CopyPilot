using System.Text;
using System.Text.RegularExpressions;
using CopyPilot.Core.Models;

namespace CopyPilot.Core.Services;

public sealed class CommandRequest
{
    public required CommandDefinition Command { get; init; }
    public LocationSpec? Source { get; init; }
    public LocationSpec? Destination { get; init; }
    public LocationSpec? Target { get; init; }
    public string JobId { get; init; } = "";

    /// <summary>Option name → value. For flags the value is "true" or "false". Empty values are ignored.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    /// <summary>Free-form extra arguments appended verbatim (split like a shell would).</summary>
    public string ExtraArguments { get; init; } = "";
}

public sealed class CommandPlan
{
    public List<string> Arguments { get; } = [];
    public List<string> Errors { get; } = [];

    /// <summary>File streamed into azcopy's stdin (pipe source).</summary>
    public string? StdinFile { get; set; }

    /// <summary>File azcopy's stdout is written to (pipe destination).</summary>
    public string? StdoutFile { get; set; }

    public bool IsValid => Errors.Count == 0;

    /// <summary>Command line as it would be typed, with secrets masked.</summary>
    public string ToDisplayString(string exe = "azcopy") =>
        CommandBuilder.ToCommandLine(exe, Arguments, mask: true)
        + (StdinFile != null ? $" < {CommandBuilder.Quote(StdinFile)}" : "")
        + (StdoutFile != null ? $" > {CommandBuilder.Quote(StdoutFile)}" : "");

    /// <summary>Command line as it would be typed, including secrets.</summary>
    public string ToUnmaskedString(string exe = "azcopy") =>
        CommandBuilder.ToCommandLine(exe, Arguments, mask: false)
        + (StdinFile != null ? $" < {CommandBuilder.Quote(StdinFile)}" : "")
        + (StdoutFile != null ? $" > {CommandBuilder.Quote(StdoutFile)}" : "");
}

public static class CommandBuilder
{
    private static readonly Regex SecretQuery = new(
        @"(?i)([?&](?:sig|signature|x-amz-signature|x-amz-credential|x-amz-security-token)=)[^&\s""]+",
        RegexOptions.Compiled);

    public static CommandPlan Build(CommandRequest request)
    {
        var plan = new CommandPlan();
        var cmd = request.Command;
        plan.Arguments.AddRange(cmd.Verbs);

        var options = request.Options
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase);

        switch (cmd.Positionals)
        {
            case PositionalKind.SourceAndDestination:
                AddSourceAndDestination(request, plan, options);
                break;
            case PositionalKind.Target:
                var target = request.Target;
                if (target == null)
                {
                    plan.Errors.Add("Choose a target.");
                    break;
                }

                if (target.Kind == LocationKind.Pipe)
                {
                    plan.Errors.Add($"'{cmd.Title}' does not support pipes.");
                    break;
                }

                if (target.IsCopyPilotHandled)
                {
                    plan.Errors.Add("SFTP and SharePoint are supported by 'Copy' and 'Sync' only.");
                    break;
                }

                plan.Errors.AddRange(target.Validate(cmd.TargetLabel));
                if (target.Build() is { Length: > 0 } t) plan.Arguments.Add(t);
                break;
            case PositionalKind.JobId:
                if (string.IsNullOrWhiteSpace(request.JobId))
                    plan.Errors.Add("Enter a job id (see 'Jobs: list').");
                else
                    plan.Arguments.Add(request.JobId.Trim());
                break;
        }

        // Emit options in catalog order so the command line is stable and readable.
        var known = OptionCatalog.Options
            .Where(o => o.AppliesTo(cmd.Id))
            .ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var def in OptionCatalog.Options.Where(o => o.AppliesTo(cmd.Id)))
        {
            if (def.CopyPilotOnly || !options.TryGetValue(def.Name, out var value)) continue;
            AddOption(plan, def, value);
        }

        foreach (var name in options.Keys.Where(k => !known.ContainsKey(k)))
        {
            plan.Errors.Add($"Option --{name} does not apply to '{cmd.Title}'.");
        }

        plan.Arguments.AddRange(SplitArguments(request.ExtraArguments));
        return plan;
    }

    private static void AddSourceAndDestination(CommandRequest request, CommandPlan plan, Dictionary<string, string> options)
    {
        var src = request.Source;
        var dst = request.Destination;
        if (src == null || dst == null)
        {
            plan.Errors.Add("Choose a source and a destination.");
            return;
        }

        plan.Errors.AddRange(src.Validate("Source"));
        plan.Errors.AddRange(dst.Validate("Destination"));

        if (src.IsCopyPilotHandled || dst.IsCopyPilotHandled)
        {
            // SFTP / SharePoint transfers are planned by TransferPlanner; azcopy never sees those locations.
            plan.Errors.Add("SFTP and SharePoint locations must be planned with TransferPlanner.");
            return;
        }

        var srcPipe = src.Kind == LocationKind.Pipe;
        var dstPipe = dst.Kind == LocationKind.Pipe;
        if (srcPipe || dstPipe)
        {
            if (request.Command.Id != CommandCatalog.Copy)
                plan.Errors.Add("Pipes are only supported by 'Copy'.");
            else if (srcPipe && dstPipe)
                plan.Errors.Add("Source and destination cannot both be a pipe.");
            else if (srcPipe && dst.Kind is not (LocationKind.AzureBlob or LocationKind.CustomUrl))
                plan.Errors.Add("A pipe source can only be uploaded to Azure Blob storage.");
            else if (dstPipe && src.Kind is not (LocationKind.AzureBlob or LocationKind.CustomUrl))
                plan.Errors.Add("Only Azure Blob storage can be downloaded to a pipe.");

            if (!options.ContainsKey("from-to"))
                options["from-to"] = srcPipe ? "PipeBlob" : "BlobPipe";
            if (srcPipe) plan.StdinFile = src.LocalPath.Trim();
            if (dstPipe) plan.StdoutFile = dst.LocalPath.Trim();
        }

        if (src.Build() is { Length: > 0 } s) plan.Arguments.Add(s);
        if (dst.Build() is { Length: > 0 } d) plan.Arguments.Add(d);
    }

    private static void AddOption(CommandPlan plan, OptionDefinition def, string value)
    {
        switch (def.Kind)
        {
            case OptionKind.Flag:
                if (bool.TryParse(value, out var b))
                    plan.Arguments.Add(b ? def.CommandLineName : def.CommandLineName + "=false");
                else
                    plan.Errors.Add($"--{def.Name} must be true or false.");
                break;
            case OptionKind.Number:
                if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                    plan.Errors.Add($"--{def.Name} must be a number (use '.' as decimal separator).");
                plan.Arguments.Add($"{def.CommandLineName}={value}");
                break;
            default:
                plan.Arguments.Add($"{def.CommandLineName}={value}");
                break;
        }
    }

    /// <summary>Splits a string into arguments, honouring double quotes.</summary>
    public static IEnumerable<string> SplitArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var sb = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '"')
            {
                sb.Append('"');
                hasToken = true;
                i++;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) yield return sb.ToString();
                sb.Clear();
                hasToken = false;
            }
            else
            {
                sb.Append(c);
                hasToken = true;
            }
        }

        if (hasToken) yield return sb.ToString();
    }

    public static string Mask(string arg)
    {
        var masked = SecretQuery.Replace(arg, "$1****");
        foreach (var def in OptionCatalog.Options.Where(o => o.Sensitive))
        {
            var prefix = def.CommandLineName + "=";
            if (masked.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return prefix + "****";
        }

        return masked;
    }

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', ';']) < 0)
            return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    public static string ToCommandLine(string exe, IEnumerable<string> args, bool mask) =>
        string.Join(' ', new[] { Quote(exe) }.Concat(args.Select(a => Quote(mask ? Mask(a) : a))));
}
