using CopyPilot.Core.Models;
using CopyPilot.Core.Services;
using Xunit;

namespace CopyPilot.Core.Tests;

public class OutputParserTests
{
    [Fact]
    public void Parses_text_progress()
    {
        var r = OutputParser.Parse("45.5 %, 10 Done, 1 Failed, 5 Pending, 2 Skipped, 18 Total, 2-sec Throughput (Mb/s): 12.3456");
        Assert.NotNull(r.Progress);
        Assert.Equal(45.5, r.Progress!.Percent);
        Assert.Equal(10, r.Progress.Done);
        Assert.Equal(1, r.Progress.Failed);
        Assert.Equal(5, r.Progress.Pending);
        Assert.Equal(2, r.Progress.Skipped);
        Assert.Equal(18, r.Progress.Total);
        Assert.Equal(12.3456, r.Progress.ThroughputMbps);
    }

    [Fact]
    public void Parses_job_start_and_final_status()
    {
        Assert.Equal("1f3c2b4a-1111-2222-3333-444455556666",
            OutputParser.Parse("INFO: Job 1f3c2b4a-1111-2222-3333-444455556666 has started").Progress!.JobId);
        var end = OutputParser.Parse("Final Job Status: CompletedWithErrors");
        Assert.Equal("CompletedWithErrors", end.Progress!.JobStatus);
        Assert.True(end.IsError);
    }

    [Fact]
    public void Parses_json_progress()
    {
        const string line = "{\"TimeStamp\":\"2026-09-30T10:00:00Z\",\"MessageType\":\"Progress\",\"MessageContent\":\"{\\\"JobID\\\":\\\"abc\\\",\\\"PercentComplete\\\":\\\"50\\\",\\\"TransfersCompleted\\\":\\\"5\\\",\\\"TransfersFailed\\\":\\\"0\\\",\\\"TransfersSkipped\\\":\\\"1\\\",\\\"TotalTransfers\\\":\\\"10\\\"}\",\"PromptDetails\":{}}";
        var r = OutputParser.Parse(line);
        Assert.Equal(50, r.Progress!.Percent);
        Assert.Equal(4, r.Progress.Pending);
        Assert.Equal("abc", r.Progress.JobId);
        Assert.StartsWith("50.0 %", r.Text);
    }

    [Fact]
    public void Plain_text_is_passed_through()
    {
        var r = OutputParser.Parse("INFO: Scanning...");
        Assert.Null(r.Progress);
        Assert.Equal("INFO: Scanning...", r.Text);
    }

    [Fact]
    public void Parses_azcopy_version()
    {
        Assert.Equal("10.30.1", AzCopyLocator.ParseVersion("azcopy version 10.30.1\n"));
    }

    [Fact]
    public void Profile_without_secrets_strips_sas_and_sensitive_values()
    {
        var p = new Profile
        {
            Source = new LocationSpec { Kind = LocationKind.AzureBlob, Account = "a", Sas = "sig=x" },
            Target = new LocationSpec { Kind = LocationKind.CustomUrl, Url = "https://x/y?sig=z" },
            Options = { ["source-sas"] = "s", ["recursive"] = "true" },
            Environment = { ["AWS_SECRET_ACCESS_KEY"] = "k", ["AWS_ACCESS_KEY_ID"] = "id" },
        };
        var json = p.ToJson(includeSecrets: false);
        Assert.DoesNotContain("sig=", json);
        Assert.DoesNotContain("source-sas", json);
        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", json);
        var back = Profile.FromJson(json);
        Assert.Equal("true", back.Options["recursive"]);
        Assert.Equal("id", back.Environment["AWS_ACCESS_KEY_ID"]);
        Assert.Equal(LocationKind.CustomUrl, back.Target.Kind);
        Assert.Contains("sig=x", p.ToJson(includeSecrets: true));
    }
}
