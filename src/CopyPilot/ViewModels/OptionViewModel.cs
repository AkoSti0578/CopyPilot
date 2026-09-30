using System.Windows.Input;
using CopyPilot.Core.Models;
using Microsoft.Win32;

namespace CopyPilot.ViewModels;

/// <summary>Editable value of one azcopy flag or environment variable.</summary>
public sealed class OptionViewModel : ObservableObject
{
    private string _value = "";

    public OptionViewModel(OptionDefinition definition, bool isEnvironment)
    {
        Definition = definition;
        IsEnvironment = isEnvironment;
        ClearCommand = new RelayCommand(() => Value = "", () => IsSet);
        BrowseCommand = new RelayCommand(Browse);
    }

    public OptionDefinition Definition { get; }
    public bool IsEnvironment { get; }

    public string Name => Definition.Name;
    public string DisplayName => IsEnvironment ? Definition.Name : Definition.CommandLineName;
    public string Category => Definition.Category;
    public string Description => Definition.Description;
    public OptionKind Kind => Definition.Kind;
    public bool IsSensitive => Definition.Sensitive;
    public IReadOnlyList<string> Choices => Definition.Choices ?? [];

    public string DefaultText => Definition.Default is { } d ? $"default: {d}" : "";

    public string AppliesToText => IsEnvironment || Definition.Commands.Contains("*")
        ? ""
        : "used by: " + string.Join(", ", Definition.Commands);

    public event EventHandler? ValueChanged;

    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value ?? "")) return;
            OnPropertyChanged(nameof(IsSet));
            OnPropertyChanged(nameof(BoolValue));
            OnPropertyChanged(nameof(FlagStateText));
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsSet => !string.IsNullOrWhiteSpace(_value);

    /// <summary>Three-state view of a flag: null = not set (azcopy default).</summary>
    public bool? BoolValue
    {
        get => bool.TryParse(_value, out var b) ? b : null;
        set => Value = value switch { true => "true", false => "false", null => "" };
    }

    public string FlagStateText => BoolValue switch
    {
        true => "on",
        false => "off",
        null => "not set" + (Definition.Default is { } d ? $" (azcopy default: {d})" : ""),
    };

    public ICommand ClearCommand { get; }
    public ICommand BrowseCommand { get; }

    private void Browse()
    {
        if (Kind == OptionKind.FolderPath)
        {
            var dlg = new OpenFolderDialog { Title = DisplayName };
            if (dlg.ShowDialog() == true) Value = dlg.FolderName;
        }
        else
        {
            var dlg = new OpenFileDialog { Title = DisplayName, CheckFileExists = false };
            if (dlg.ShowDialog() == true) Value = dlg.FileName;
        }
    }
}
