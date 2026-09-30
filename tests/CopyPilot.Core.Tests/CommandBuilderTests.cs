using CopyPilot.Core.Models;
using CopyPilot.Core.Services;
using Xunit;

namespace CopyPilot.Core.Tests;

public class CommandBuilderTests
{
    [Fact]
    public void Copy_local_to_blob_with_options()
    {
        var plan = CommandBuilder.Build(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy),
            Source = new LocationSpec { Kind = LocationKind.Local, LocalPath = @"C:\My Data\*" },
            Destination = new LocationSpec
            {
                Kind = LocationKind.AzureBlob, Account = "acct", Container = "backup", Path = "2026/q3 files",
                Sas = "?sv=2022&sig=SECRET",
            },
            Options = new Dictionary<string, string>
            {
                ["recursive"] = "true",
                ["overwrite"] = "ifSourceNewer",
                ["as-subdir"] = "false",
                ["sync-only-flag"] = "",
            },
            ExtraArguments = "--foo=\"a b\" --bar",
        });

        Assert.True(plan.IsValid, string.Join(", ", plan.Errors));
        Assert.Equal(
            ["copy", @"C:\My Data\*", "https://acct.blob.core.windows.net/backup/2026/q3%20files?sv=2022&sig=SECRET",
             "--recursive", "--as-subdir=false", "--overwrite=ifSourceNewer", "--foo=a b", "--bar"],
            plan.Arguments);
        Assert.DoesNotContain("SECRET", plan.ToDisplayString());
        Assert.Contains("sig=****", plan.ToDisplayString());
        Assert.Contains("SECRET", plan.ToUnmaskedString());
    }

    [Fact]
    public void Option_not_valid_for_command_is_reported()
    {
        var plan = CommandBuilder.Build(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Make),
            Target = new LocationSpec { Kind = LocationKind.AzureFiles, Account = "a", Container = "share" },
            Options = new Dictionary<string, string> { ["delete-destination"] = "true" },
        });
        Assert.False(plan.IsValid);
    }

    [Fact]
    public void Pipe_source_sets_from_to_and_stdin()
    {
        var plan = CommandBuilder.Build(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy),
            Source = new LocationSpec { Kind = LocationKind.Pipe, LocalPath = "in.bin" },
            Destination = new LocationSpec { Kind = LocationKind.AzureBlob, Account = "a", Container = "c", Path = "blob.bin" },
        });
        Assert.True(plan.IsValid, string.Join(", ", plan.Errors));
        Assert.Equal("in.bin", plan.StdinFile);
        Assert.Equal(["copy", "https://a.blob.core.windows.net/c/blob.bin", "--from-to=PipeBlob"], plan.Arguments);
    }

    [Fact]
    public void Pipe_to_s3_is_rejected()
    {
        var plan = CommandBuilder.Build(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Copy),
            Source = new LocationSpec { Kind = LocationKind.Pipe, LocalPath = "in.bin" },
            Destination = new LocationSpec { Kind = LocationKind.AmazonS3, Container = "b" },
        });
        Assert.False(plan.IsValid);
    }

    [Fact]
    public void Jobs_resume_masks_sas_and_requires_job_id()
    {
        var cmd = CommandCatalog.Get(CommandCatalog.JobsResume);
        Assert.False(CommandBuilder.Build(new CommandRequest { Command = cmd }).IsValid);

        var plan = CommandBuilder.Build(new CommandRequest
        {
            Command = cmd,
            JobId = "1234",
            Options = new Dictionary<string, string> { ["source-sas"] = "sv=1&sig=x" },
        });
        Assert.Equal(["jobs", "resume", "1234", "--source-sas=sv=1&sig=x"], plan.Arguments);
        Assert.Equal("azcopy jobs resume 1234 --source-sas=****", plan.ToDisplayString());
    }

    [Fact]
    public void Invalid_number_is_reported()
    {
        var plan = CommandBuilder.Build(new CommandRequest
        {
            Command = CommandCatalog.Get(CommandCatalog.Login),
            Options = new Dictionary<string, string> { ["cap-mbps"] = "fast" },
        });
        Assert.False(plan.IsValid);
    }

    [Theory]
    [InlineData(LocationKind.AzureDataLakeGen2, "https://acct.dfs.core.windows.net/fs/dir/*.csv")]
    [InlineData(LocationKind.AzureFiles, "https://acct.file.core.windows.net/fs/dir/*.csv")]
    [InlineData(LocationKind.AmazonS3, "https://s3.eu-west-1.amazonaws.com/fs/dir/*.csv")]
    [InlineData(LocationKind.GoogleCloudStorage, "https://storage.cloud.google.com/fs/dir/*.csv")]
    public void Urls_are_built_per_service(LocationKind kind, string expected)
    {
        var l = new LocationSpec { Kind = kind, Account = "acct", Container = "fs", Path = @"\dir\*.csv", Region = "eu-west-1" };
        Assert.Equal(expected, l.Build());
    }

    [Fact]
    public void Account_level_url_has_trailing_slash()
    {
        var l = new LocationSpec { Kind = LocationKind.AzureBlob, Account = "acct", EndpointSuffix = "core.chinacloudapi.cn", Sas = "sv=1" };
        Assert.Equal("https://acct.blob.core.chinacloudapi.cn/?sv=1", l.Build());
    }

    [Fact]
    public void Every_command_has_unique_option_names()
    {
        foreach (var cmd in CommandCatalog.All)
        {
            var names = OptionCatalog.For(cmd.Id).Select(o => o.Name).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void Split_arguments_honours_quotes()
    {
        Assert.Equal(["a", "b c", "", "d\"e"], CommandBuilder.SplitArguments("a \"b c\" \"\" d\\\"e"));
    }
}

public class ScriptExporterTests
{
    [Fact]
    public void Cmd_escapes_percent_and_ps1_quotes()
    {
        var plan = new CommandPlan();
        plan.Arguments.AddRange(["copy", "C:\\it's here", "https://a.blob.core.windows.net/c?sig=a%2Fb"]);
        var env = new Dictionary<string, string> { ["AZCOPY_CONCURRENCY_VALUE"] = "16" };

        var cmd = ScriptExporter.ToCmd(@"C:\Program Files\CopyPilot\azcopy\azcopy.exe", plan, env);
        Assert.Contains("sig=a%%2Fb", cmd);
        Assert.Contains("set \"AZCOPY_CONCURRENCY_VALUE=16\"", cmd);
        Assert.Contains("\"C:\\Program Files\\CopyPilot\\azcopy\\azcopy.exe\" copy", cmd);

        var ps = ScriptExporter.ToPowerShell("azcopy.exe", plan, env);
        Assert.Contains("'C:\\it''s here'", ps);
        Assert.Contains("$env:AZCOPY_CONCURRENCY_VALUE = '16'", ps);
    }
}
