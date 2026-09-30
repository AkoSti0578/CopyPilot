using System.Windows.Input;
using CopyPilot.Core.Models;
using CopyPilot.Core.Services;
using Microsoft.Win32;

namespace CopyPilot.ViewModels;

public sealed record LocationKindItem(LocationKind Kind, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Editor state for a source, destination or target location.</summary>
public sealed class LocationViewModel : ObservableObject
{
    private LocationSpec _spec;

    public LocationViewModel(string header, LocationSpec spec, bool isDestination)
    {
        Header = header;
        IsDestination = isDestination;
        _spec = spec;
        BrowseFileCommand = new RelayCommand(BrowseFile);
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
    }

    public string Header { get; }
    public bool IsDestination { get; }

    public static IReadOnlyList<LocationKindItem> Kinds { get; } =
    [
        new(LocationKind.Local, "Local file / folder / network share (UNC)"),
        new(LocationKind.AzureBlob, "Azure Blob Storage"),
        new(LocationKind.AzureDataLakeGen2, "Azure Data Lake Storage Gen2 (dfs)"),
        new(LocationKind.AzureFiles, "Azure Files (SMB / NFS share)"),
        new(LocationKind.AmazonS3, "Amazon S3"),
        new(LocationKind.GoogleCloudStorage, "Google Cloud Storage"),
        new(LocationKind.CustomUrl, "Any URL (paste a full URL, e.g. with SAS)"),
        new(LocationKind.Pipe, "Pipe (stream from / to a file via stdin / stdout)"),
    ];

    public static IReadOnlyList<string> EndpointSuffixes => LocationSpec.AzureEndpointSuffixes;

    public event EventHandler? Changed;

    public LocationSpec Spec
    {
        get => _spec;
        set
        {
            _spec = value.Clone();
            OnPropertyChanged(string.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public LocationKindItem SelectedKind
    {
        get => Kinds.First(k => k.Kind == _spec.Kind);
        set
        {
            if (value == null || value.Kind == _spec.Kind) return;
            _spec.Kind = value.Kind;
            OnPropertyChanged(string.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public string LocalPath { get => _spec.LocalPath; set => Update(() => _spec.LocalPath = value); }
    public string Account { get => _spec.Account; set => Update(() => _spec.Account = value); }
    public string Container { get => _spec.Container; set => Update(() => _spec.Container = value); }
    public string Path { get => _spec.Path; set => Update(() => _spec.Path = value); }
    public string Sas { get => _spec.Sas; set => Update(() => _spec.Sas = value); }
    public string EndpointSuffix { get => _spec.EndpointSuffix; set => Update(() => _spec.EndpointSuffix = value); }
    public string Region { get => _spec.Region; set => Update(() => _spec.Region = value); }
    public string Url { get => _spec.Url; set => Update(() => _spec.Url = value); }

    public bool IsLocal => _spec.Kind == LocationKind.Local;
    public bool IsPipe => _spec.Kind == LocationKind.Pipe;
    public bool IsAzure => _spec.Kind is LocationKind.AzureBlob or LocationKind.AzureDataLakeGen2 or LocationKind.AzureFiles;
    public bool IsS3 => _spec.Kind == LocationKind.AmazonS3;
    public bool IsBucketBased => _spec.Kind is LocationKind.AmazonS3 or LocationKind.GoogleCloudStorage;
    public bool IsCustomUrl => _spec.Kind == LocationKind.CustomUrl;
    public bool HasContainer => IsAzure || IsBucketBased;
    public bool UsesLocalPath => IsLocal || IsPipe;
    public string LocalPathLabel => IsPipe ? (IsDestination ? "Save the stream to file" : "File to stream") : "Local path";

    public string ContainerLabel => _spec.Kind switch
    {
        LocationKind.AzureBlob => "Container",
        LocationKind.AzureDataLakeGen2 => "File system",
        LocationKind.AzureFiles => "File share",
        _ => "Bucket",
    };

    public string ContainerHint => IsAzure
        ? "Leave empty to use the whole storage account."
        : "Leave empty to use every bucket in the account/project.";

    public string AuthHint => _spec.Kind switch
    {
        LocationKind.AmazonS3 => "Authentication: set AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY on the Environment tab. S3 can only be a source (to Azure Blob).",
        LocationKind.GoogleCloudStorage => "Authentication: set GOOGLE_APPLICATION_CREDENTIALS on the Environment tab. GCS can only be a source (to Azure Blob).",
        LocationKind.AzureBlob or LocationKind.AzureDataLakeGen2 or LocationKind.AzureFiles =>
            "Authentication: use a SAS token, or leave it empty and sign in with 'Login' / AZCOPY_AUTO_LOGIN_TYPE (Microsoft Entra ID).",
        LocationKind.Pipe => IsDestination
            ? "azcopy writes the downloaded blob to stdout; CopyPilot saves it to this file. Only works with an Azure Blob source."
            : "CopyPilot streams this file into azcopy's stdin. Only works with an Azure Blob destination.",
        LocationKind.Local => "Use wildcards like C:\\data\\*.csv. UNC paths (\\\\server\\share\\folder) are supported.",
        _ => "",
    };

    /// <summary>What azcopy will receive, with secrets masked.</summary>
    public string Preview
    {
        get
        {
            var built = _spec.Build();
            if (built == null) return IsDestination ? "(stdout → " + LocalPath + ")" : "(stdin ← " + LocalPath + ")";
            return CommandBuilder.Mask(built);
        }
    }

    public ICommand BrowseFileCommand { get; }
    public ICommand BrowseFolderCommand { get; }

    private void Update(Action apply)
    {
        apply();
        OnPropertyChanged(nameof(Preview));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void BrowseFile()
    {
        if (IsDestination && IsPipe)
        {
            var save = new SaveFileDialog { Title = Header, FileName = System.IO.Path.GetFileName(LocalPath) };
            if (save.ShowDialog() == true) LocalPath = save.FileName;
            OnPropertyChanged(nameof(LocalPath));
            return;
        }

        var dlg = new OpenFileDialog { Title = Header, CheckFileExists = !IsDestination };
        if (dlg.ShowDialog() == true) LocalPath = dlg.FileName;
        OnPropertyChanged(nameof(LocalPath));
    }

    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog { Title = Header };
        if (dlg.ShowDialog() == true) LocalPath = dlg.FolderName;
        OnPropertyChanged(nameof(LocalPath));
    }
}
