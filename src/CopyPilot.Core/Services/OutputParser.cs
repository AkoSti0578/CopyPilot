using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CopyPilot.Core.Services;

public sealed record ProgressInfo
{
    public double? Percent { get; init; }
    public long? Done { get; init; }
    public long? Failed { get; init; }
    public long? Pending { get; init; }
    public long? Skipped { get; init; }
    public long? Total { get; init; }
    public double? ThroughputMbps { get; init; }
    public string? JobId { get; init; }
    public string? JobStatus { get; init; }
}

/// <summary>A parsed line of azcopy output.</summary>
/// <param name="Text">Text to show in the log (null = nothing to show).</param>
/// <param name="Progress">Progress info extracted from the line, if any.</param>
/// <param name="IsError">The line reports an error.</param>
public sealed record OutputLine(string? Text, ProgressInfo? Progress, bool IsError = false);

/// <summary>Parses azcopy's text and JSON output into log lines and progress updates.</summary>
public static partial class OutputParser
{
    [GeneratedRegex(@"^\s*(?<pct>\d+(?:\.\d+)?)\s*%,\s*(?<done>\d+)\s+Done,\s*(?<failed>\d+)\s+Failed,\s*(?<pending>\d+)\s+Pending,(?:\s*(?<skipped>\d+)\s+Skipped,)?\s*(?<total>\d+)\s+Total(?:.*?Throughput \(Mb/s\):\s*(?<tp>[\d.]+))?")]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"Job (?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}) has started")]
    private static partial Regex JobStartedRegex();

    [GeneratedRegex(@"^\s*Final Job Status:\s*(?<status>\w+)")]
    private static partial Regex FinalStatusRegex();

    public static OutputLine Parse(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('{') && TryParseJson(trimmed, out var json))
            return json;
        return ParseText(line);
    }

    private static OutputLine ParseText(string line)
    {
        var m = ProgressRegex().Match(line);
        if (m.Success)
        {
            return new OutputLine(line, new ProgressInfo
            {
                Percent = D(m.Groups["pct"].Value),
                Done = L(m.Groups["done"].Value),
                Failed = L(m.Groups["failed"].Value),
                Pending = L(m.Groups["pending"].Value),
                Skipped = m.Groups["skipped"].Success ? L(m.Groups["skipped"].Value) : null,
                Total = L(m.Groups["total"].Value),
                ThroughputMbps = m.Groups["tp"].Success ? D(m.Groups["tp"].Value) : null,
            });
        }

        m = JobStartedRegex().Match(line);
        if (m.Success)
            return new OutputLine(line, new ProgressInfo { JobId = m.Groups["id"].Value });

        m = FinalStatusRegex().Match(line);
        if (m.Success)
        {
            var status = m.Groups["status"].Value;
            return new OutputLine(line, new ProgressInfo { JobStatus = status },
                status.Contains("Fail", StringComparison.OrdinalIgnoreCase) || status.Contains("Error", StringComparison.OrdinalIgnoreCase));
        }

        var isError = line.Contains("failed to", StringComparison.OrdinalIgnoreCase)
                      || line.StartsWith("error", StringComparison.OrdinalIgnoreCase)
                      || line.Contains("RESPONSE ERROR", StringComparison.OrdinalIgnoreCase);
        return new OutputLine(line, null, isError);
    }

    private static bool TryParseJson(string line, out OutputLine result)
    {
        result = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("MessageType", out var typeEl))
                return false;

            var type = typeEl.GetString() ?? "";
            var content = root.TryGetProperty("MessageContent", out var c) ? c.GetString() ?? "" : "";

            ProgressInfo? progress = null;
            if (type is "Progress" or "EndOfJob" or "Init" && content.TrimStart().StartsWith('{'))
            {
                using var inner = JsonDocument.Parse(content);
                var r = inner.RootElement;
                progress = new ProgressInfo
                {
                    JobId = S(r, "JobID"),
                    Percent = N(r, "PercentComplete"),
                    Done = (long?)N(r, "TransfersCompleted"),
                    Failed = (long?)N(r, "TransfersFailed"),
                    Skipped = (long?)N(r, "TransfersSkipped"),
                    Total = (long?)N(r, "TotalTransfers"),
                    ThroughputMbps = N(r, "Throughput") ?? N(r, "ThroughputMbps"),
                    JobStatus = type == "EndOfJob" ? S(r, "JobStatus") : null,
                };
                if (progress.Total is { } total && progress.Done is { } done)
                    progress = progress with { Pending = Math.Max(0, total - done - (progress.Failed ?? 0) - (progress.Skipped ?? 0)) };
            }

            string? text = type switch
            {
                "Progress" when progress != null => FormatProgress(progress),
                "EndOfJob" when progress != null => $"Final Job Status: {progress.JobStatus} — {progress.Done} done, {progress.Failed} failed, {progress.Skipped} skipped, {progress.Total} total",
                "Init" when progress?.JobId != null => $"Job {progress.JobId} has started",
                _ => string.IsNullOrWhiteSpace(content) ? null : content.TrimEnd(),
            };
            var isError = type == "Error" || (progress?.Failed ?? 0) > 0 && type == "EndOfJob";
            result = new OutputLine(text, progress, isError);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string FormatProgress(ProgressInfo p) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{p.Percent ?? 0:0.0} %, {p.Done ?? 0} Done, {p.Failed ?? 0} Failed, {p.Pending ?? 0} Pending, {p.Skipped ?? 0} Skipped, {p.Total ?? 0} Total{(p.ThroughputMbps is { } t ? $", Throughput (Mb/s): {t:0.##}" : "")}");

    private static double? D(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static long? L(string s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;

    private static string? S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    private static double? N(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => D(v.GetString() ?? ""),
            _ => null,
        };
    }
}
