using System.Text.Json;
using System.Text.Json.Serialization;
using CopyPilot.Core.Models;

namespace CopyPilot.Core.Services;

/// <summary>A saved CopyPilot job definition (everything needed to rebuild the command).</summary>
public sealed class Profile
{
    public string CommandId { get; set; } = CommandCatalog.Copy;
    public LocationSpec Source { get; set; } = new();
    public LocationSpec Destination { get; set; } = new() { Kind = LocationKind.AzureBlob };
    public LocationSpec Target { get; set; } = new() { Kind = LocationKind.AzureBlob };
    public string JobId { get; set; } = "";
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ExtraArguments { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes the profile. Unless <paramref name="includeSecrets"/> is set, SAS tokens and sensitive values are removed.</summary>
    public string ToJson(bool includeSecrets)
    {
        var copy = includeSecrets ? this : WithoutSecrets();
        return JsonSerializer.Serialize(copy, JsonOptions);
    }

    public static Profile FromJson(string json) =>
        JsonSerializer.Deserialize<Profile>(json, JsonOptions) ?? new Profile();

    public Profile WithoutSecrets()
    {
        var sensitiveOptions = OptionCatalog.Options.Where(o => o.Sensitive).Select(o => o.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sensitiveEnv = EnvironmentCatalog.Variables.Where(o => o.Sensitive).Select(o => o.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        static LocationSpec Strip(LocationSpec l)
        {
            var c = l.Clone();
            c.Sas = "";
            c.Url = StripQuery(c.Url);
            return c;
        }

        return new Profile
        {
            CommandId = CommandId,
            Source = Strip(Source),
            Destination = Strip(Destination),
            Target = Strip(Target),
            JobId = JobId,
            Options = Options.Where(kv => !sensitiveOptions.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            Environment = Environment.Where(kv => !sensitiveEnv.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            ExtraArguments = ExtraArguments,
        };
    }

    private static string StripQuery(string url)
    {
        var i = url.IndexOf('?');
        return i < 0 ? url : url[..i];
    }
}
