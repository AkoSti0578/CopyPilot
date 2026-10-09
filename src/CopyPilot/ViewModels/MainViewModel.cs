using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CopyPilot.Core.Models;
using CopyPilot.Core.Services;
using CopyPilot.Services;
using Microsoft.Win32;

namespace CopyPilot.ViewModels;

public enum LogKind
{
    Info,
    Warning,
    Error,
    Command,
    Progress,
    Success,
}

/// <summary>One output line. A class (not a record) so identical lines stay distinct items in the list.</summary>
public sealed class LogLine(string text, LogKind kind) : ObservableObject
{
    private string _text = text;

    /// <summary>Mutable so the progress line can be updated in place (no Replace events on the list).</summary>
    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public LogKind Kind { get; } = kind;
}

public sealed class MainViewModel : ObservableObject
{
    public const string Author = "Arnold Kolkman";
    private const int MaxLogLines = 20000;

    public const int TabTransfer = 0;
    public const int TabLog = 4;

    private readonly AppSettings _settings;
    private readonly AzCopyRunner _runner = new();
    private readonly TransferRunner _transferRunner;
    private readonly ConcurrentQueue<OutputLine> _pending = new();
    private readonly DispatcherTimer _flushTimer;

    private CommandDefinition _selectedCommand;
    private string _jobId = "";
    private string _optionSearch = "";
    private bool _showOnlySetOptions;
    private string _extraArguments = "";
    private string _commandPreview = "";
    private string _validationText = "";
    private bool _isValid;
    private bool _isRunning;
    private double _percent;
    private bool _isIndeterminate;
    private string _statusText = "Ready.";
    private string _countsText = "";
    private string _runningJobId = "";
    private string _inputText = "";
    private string _resolvedAzCopyPath = "";
    private string _azCopyVersion = "unknown";
    private int _selectedTabIndex;
    private CancellationTokenSource? _cts;
    private bool _suspendRefresh;

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _transferRunner = new TransferRunner(_runner)
        {
            KnownHostsFile = Path.Combine(AppSettings.Folder, "known_hosts"),
            TokenCacheFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CopyPilot", "msal-token-cache.bin"),
        };
        _selectedCommand = CommandCatalog.Get(CommandCatalog.Copy);

        Source = new LocationViewModel("Source", new LocationSpec { Kind = LocationKind.Local }, isDestination: false);
        Destination = new LocationViewModel("Destination", new LocationSpec { Kind = LocationKind.AzureBlob }, isDestination: true);
        Target = new LocationViewModel("Target", new LocationSpec { Kind = LocationKind.AzureBlob }, isDestination: false);
        Source.Changed += (_, _) => Refresh();
        Destination.Changed += (_, _) => Refresh();
        Target.Changed += (_, _) => Refresh();

        AllOptions = OptionCatalog.Options.Select(o => new OptionViewModel(o, isEnvironment: false)).ToList();
        foreach (var o in AllOptions) o.ValueChanged += (_, _) => Refresh();
        OptionsView = new ListCollectionView(AllOptions);
        OptionsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OptionViewModel.Category)));
        OptionsView.Filter = FilterOption;

        EnvironmentVariables = EnvironmentCatalog.Variables.Select(o => new OptionViewModel(o, isEnvironment: true)).ToList();
        foreach (var o in EnvironmentVariables) o.ValueChanged += (_, _) => Refresh();
        EnvironmentView = new ListCollectionView(EnvironmentVariables);
        EnvironmentView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OptionViewModel.Category)));

        RunCommand = new RelayCommand(async () => await RunAsync(), () => !IsRunning);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        CopyCommandLineCommand = new RelayCommand(() => CopyToClipboard(BuildPlan().ToDisplayString(AzCopyDisplayName)));
        CopyCommandLineWithSecretsCommand = new RelayCommand(() => CopyToClipboard(BuildPlan().ToUnmaskedString(ResolvedOrDefaultExe)));
        SaveScriptCommand = new RelayCommand(SaveScript);
        SaveProfileCommand = new RelayCommand(SaveProfile);
        LoadProfileCommand = new RelayCommand(LoadProfile);
        NewProfileCommand = new RelayCommand(NewProfile);
        ResetOptionsCommand = new RelayCommand(() => ForEachOption(AllOptions.Where(o => o.Definition.AppliesTo(SelectedCommand.Id)), o => o.Value = ""));
        ResetEnvironmentCommand = new RelayCommand(() => ForEachOption(EnvironmentVariables, o => o.Value = ""));
        SwapLocationsCommand = new RelayCommand(SwapLocations);
        ClearLogCommand = new RelayCommand(() => Log.Clear());
        SaveLogCommand = new RelayCommand(SaveLog);
        CopyLogCommand = new RelayCommand(() => CopyToClipboard(string.Join(Environment.NewLine, Log.Select(l => l.Text))));
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        SendInputCommand = new RelayCommand(async () => await SendInputAsync(), () => IsRunning);
        BrowseAzCopyCommand = new RelayCommand(BrowseAzCopy);
        ResetAzCopyCommand = new RelayCommand(() => { AzCopyPathOverride = ""; });
        UpdateAzCopyCommand = new RelayCommand(async () => await UpdateAzCopyAsync(), () => !IsRunning);
        OpenUrlCommand = new RelayCommand(p => OpenUrl(p as string));
        QuickCommand = new RelayCommand(p => QuickRun(p as string), _ => !IsRunning);

        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => FlushOutput(),
            Dispatcher.CurrentDispatcher);
        _flushTimer.Start();

        if (AppSettings.LoadLastSession() is { } last) ApplyProfile(last);

        ResolveAzCopy();
        Refresh();
    }

    // ---- Static info ------------------------------------------------------------------------

    public string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0.0";

    public string AuthorName => Author;
    public string CopyrightText => $"© {DateTime.Now.Year} {Author}. All rights reserved.";
    public string WindowTitle => $"CopyPilot {AppVersion} — by {Author}";

    // ---- Command selection -----------------------------------------------------------------

    public IReadOnlyList<CommandDefinition> AvailableCommands => CommandCatalog.All;

    public CommandDefinition SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (value == null || !Set(ref _selectedCommand, value)) return;
            OnPropertyChanged(nameof(ShowSourceAndDestination));
            OnPropertyChanged(nameof(ShowTarget));
            OnPropertyChanged(nameof(ShowJobId));
            OnPropertyChanged(nameof(ShowNoLocation));
            OnPropertyChanged(nameof(TargetLabel));
            OptionsView.Refresh();
            OnPropertyChanged(nameof(OptionCountText));
            Refresh();
        }
    }

    public bool ShowSourceAndDestination => SelectedCommand.Positionals == PositionalKind.SourceAndDestination;
    public bool ShowTarget => SelectedCommand.Positionals == PositionalKind.Target;
    public bool ShowJobId => SelectedCommand.Positionals == PositionalKind.JobId;
    public bool ShowNoLocation => SelectedCommand.Positionals == PositionalKind.None;
    public string TargetLabel => SelectedCommand.TargetLabel;

    public LocationViewModel Source { get; }
    public LocationViewModel Destination { get; }
    public LocationViewModel Target { get; }

    public string JobId
    {
        get => _jobId;
        set
        {
            if (Set(ref _jobId, value)) Refresh();
        }
    }

    // ---- Options ----------------------------------------------------------------------------

    public List<OptionViewModel> AllOptions { get; }
    public ListCollectionView OptionsView { get; }
    public List<OptionViewModel> EnvironmentVariables { get; }
    public ListCollectionView EnvironmentView { get; }

    public string OptionSearch
    {
        get => _optionSearch;
        set
        {
            if (!Set(ref _optionSearch, value)) return;
            OptionsView.Refresh();
            OnPropertyChanged(nameof(OptionCountText));
        }
    }

    public bool ShowOnlySetOptions
    {
        get => _showOnlySetOptions;
        set
        {
            if (!Set(ref _showOnlySetOptions, value)) return;
            OptionsView.Refresh();
            OnPropertyChanged(nameof(OptionCountText));
        }
    }

    public string OptionCountText
    {
        get
        {
            var applicable = AllOptions.Count(o => o.Definition.AppliesTo(SelectedCommand.Id));
            var set = AllOptions.Count(o => o.IsSet && o.Definition.AppliesTo(SelectedCommand.Id));
            return $"{applicable} options available for '{SelectedCommand.Title}', {set} set";
        }
    }

    public string EnvironmentCountText => $"{EnvironmentVariables.Count(e => e.IsSet)} environment variable(s) set for this run";

    private bool FilterOption(object item)
    {
        var o = (OptionViewModel)item;
        if (!o.Definition.AppliesTo(SelectedCommand.Id)) return false;
        if (ShowOnlySetOptions && !o.IsSet) return false;
        if (string.IsNullOrWhiteSpace(OptionSearch)) return true;
        var q = OptionSearch.Trim();
        return o.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || o.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
               || o.Category.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public string ExtraArguments
    {
        get => _extraArguments;
        set
        {
            if (Set(ref _extraArguments, value)) Refresh();
        }
    }

    // ---- Preview ----------------------------------------------------------------------------

    public string CommandPreview { get => _commandPreview; private set => Set(ref _commandPreview, value); }
    public string ValidationText { get => _validationText; private set => Set(ref _validationText, value); }
    public bool IsValid { get => _isValid; private set => Set(ref _isValid, value); }

    private CommandRequest BuildRequest() => new()
    {
        Command = SelectedCommand,
        Source = Source.Spec,
        Destination = Destination.Spec,
        Target = Target.Spec,
        JobId = JobId,
        Options = AllOptions
            .Where(o => o.IsSet && o.Definition.AppliesTo(SelectedCommand.Id))
            .ToDictionary(o => o.Name, o => o.Value),
        ExtraArguments = ExtraArguments,
    };

    private ExecutionPlan BuildPlan() => TransferPlanner.Plan(BuildRequest());

    private Dictionary<string, string> BuildEnvironment() =>
        EnvironmentVariables.Where(e => e.IsSet).ToDictionary(e => e.Name, e => e.Value.Trim());

    private void Refresh()
    {
        if (_suspendRefresh) return;
        var plan = BuildPlan();
        var env = BuildEnvironment();
        var sb = new StringBuilder();
        foreach (var (k, v) in env)
        {
            var sensitive = EnvironmentCatalog.Variables.Any(d => d.Sensitive && d.Name == k);
            sb.Append("set ").Append(k).Append('=').Append(sensitive ? "****" : v).AppendLine();
        }

        sb.Append(plan.ToDisplayString(AzCopyDisplayName));
        CommandPreview = sb.ToString();
        IsValid = plan.IsValid;
        ValidationText = !plan.IsValid ? "⚠ " + string.Join("  ", plan.Errors)
            : plan.Warnings.Count > 0 ? "✔ Ready to run.  ⓘ " + string.Join("  ", plan.Warnings)
            : plan.IsSftp ? "✔ Ready to run (SFTP / SharePoint is handled by CopyPilot" + (plan.NeedsAzCopy ? " together with azcopy)." : ").")
            : "✔ Command is ready to run.";
        OnPropertyChanged(nameof(OptionCountText));
        OnPropertyChanged(nameof(EnvironmentCountText));
    }

    // ---- azcopy executable -------------------------------------------------------------------

    public string AzCopyPathOverride
    {
        get => _settings.AzCopyPath;
        set
        {
            if (_settings.AzCopyPath == value) return;
            _settings.AzCopyPath = value ?? "";
            _settings.Save();
            OnPropertyChanged();
            ResolveAzCopy();
        }
    }

    public string ResolvedAzCopyPath { get => _resolvedAzCopyPath; private set => Set(ref _resolvedAzCopyPath, value); }
    public string AzCopyVersion { get => _azCopyVersion; private set => Set(ref _azCopyVersion, value); }

    private string AzCopyDisplayName => "azcopy";
    private string ResolvedOrDefaultExe => string.IsNullOrEmpty(ResolvedAzCopyPath) ? "azcopy" : ResolvedAzCopyPath;

    private async void ResolveAzCopy()
    {
        var found = AzCopyLocator.Find(_settings.AzCopyPath);
        ResolvedAzCopyPath = found ?? "";
        if (found == null)
        {
            AzCopyVersion = "not found";
            StatusText = "azcopy.exe was not found. Use Settings to download it or point to an existing azcopy.exe.";
            return;
        }

        AzCopyVersion = "checking…";
        AzCopyVersion = await AzCopyLocator.GetVersionAsync(found) ?? "unknown";
    }

    // ---- Running -----------------------------------------------------------------------------

    public ObservableCollection<LogLine> Log { get; } = [];

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string CountsText { get => _countsText; private set => Set(ref _countsText, value); }
    public string RunningJobId { get => _runningJobId; private set => Set(ref _runningJobId, value); }
    public string InputText { get => _inputText; set => Set(ref _inputText, value); }
    public int SelectedTabIndex { get => _selectedTabIndex; set => Set(ref _selectedTabIndex, value); }

    public ICommand RunCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CopyCommandLineCommand { get; }
    public ICommand CopyCommandLineWithSecretsCommand { get; }
    public ICommand SaveScriptCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand NewProfileCommand { get; }
    public ICommand ResetOptionsCommand { get; }
    public ICommand ResetEnvironmentCommand { get; }
    public ICommand SwapLocationsCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand SaveLogCommand { get; }
    public ICommand CopyLogCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand SendInputCommand { get; }
    public ICommand BrowseAzCopyCommand { get; }
    public ICommand ResetAzCopyCommand { get; }
    public ICommand UpdateAzCopyCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand QuickCommand { get; }

    private async Task RunAsync()
    {
        var plan = BuildPlan();
        if (!plan.IsValid)
        {
            MessageBox.Show(string.Join(Environment.NewLine, plan.Errors), "CopyPilot", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var exe = AzCopyLocator.Find(_settings.AzCopyPath);
        if (exe == null && plan.NeedsAzCopy)
        {
            MessageBox.Show("azcopy.exe was not found.\n\nOpen the Settings tab and click 'Download latest azcopy', or browse to an existing azcopy.exe.",
                "CopyPilot", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await RunPlanAsync(exe, plan);
    }

    private async Task RunPlanAsync(string? exe, ExecutionPlan plan)
    {
        IsRunning = true;
        _cts = new CancellationTokenSource();
        Percent = 0;
        IsIndeterminate = true;
        CountsText = "";
        RunningJobId = "";
        StatusText = $"Running '{plan.Request.Command.Title}'" + (plan.IsSftp ? " (CopyPilot transfer)" : "") + " …";
        SelectedTabIndex = TabLog;
        foreach (var line in plan.ToDisplayString(exe ?? "azcopy").Split('\n'))
            AddLog(new LogLine("> " + line.TrimEnd('\r'), LogKind.Command));
        foreach (var warning in plan.Warnings) AddLog(new LogLine("WARNING: " + warning, LogKind.Warning));

        var sw = Stopwatch.StartNew();
        try
        {
            // Run on the thread pool so SFTP work never blocks the UI thread.
            var env = BuildEnvironment();
            var token = _cts.Token;
            var code = await Task.Run(() => _transferRunner.RunAsync(plan, exe, env, OnOutput, token), token);
            FlushOutput();
            if (code == 0)
            {
                if (Percent > 0 || !IsIndeterminate) Percent = 100;
                StatusText = $"Finished successfully in {sw.Elapsed:hh\\:mm\\:ss}.";
                AddLog(new LogLine(StatusText, LogKind.Success));
            }
            else
            {
                StatusText = (plan.IsSftp ? $"The transfer finished with errors (code {code})" : $"azcopy exited with code {code}")
                             + $" after {sw.Elapsed:hh\\:mm\\:ss}. See the output above" + (plan.NeedsAzCopy ? " and the azcopy log file." : ".");
                AddLog(new LogLine(StatusText, LogKind.Error));
            }
        }
        catch (OperationCanceledException)
        {
            FlushOutput();
            StatusText = string.IsNullOrEmpty(RunningJobId)
                ? "Cancelled."
                : $"Cancelled. Resume later with 'Jobs: resume' and job id {RunningJobId}.";
            AddLog(new LogLine(StatusText, LogKind.Warning));
        }
        catch (Exception ex)
        {
            FlushOutput();
            StatusText = (plan.IsSftp ? "Transfer failed: " : "Failed to run azcopy: ") + ex.Message;
            AddLog(new LogLine(StatusText, LogKind.Error));
        }
        finally
        {
            IsIndeterminate = false;
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void OnOutput(string line, bool isStdErr)
    {
        _pending.Enqueue(OutputParser.Parse(line));
    }

    private void FlushOutput()
    {
        while (_pending.TryDequeue(out var o))
        {
            if (o.Progress is { } p)
            {
                if (p.Percent is { } pct)
                {
                    IsIndeterminate = false;
                    Percent = Math.Clamp(pct, 0, 100);
                    CountsText = $"{p.Done ?? 0} done · {p.Failed ?? 0} failed · {p.Skipped ?? 0} skipped · {p.Pending ?? 0} pending · {p.Total ?? 0} total"
                                 + (p.ThroughputMbps is { } t ? $" · {t:0.##} Mb/s" : "");
                }

                if (!string.IsNullOrEmpty(p.JobId)) RunningJobId = p.JobId;
                if (!string.IsNullOrEmpty(p.JobStatus)) StatusText = "Final job status: " + p.JobStatus;
            }

            if (o.Text == null) continue;
            var kind = o.IsError ? LogKind.Error
                : o.Progress?.Percent != null ? LogKind.Progress
                : o.Text.StartsWith("WARN", StringComparison.OrdinalIgnoreCase) ? LogKind.Warning
                : LogKind.Info;

            // Keep a single, updating progress line instead of hundreds of them.
            if (kind == LogKind.Progress && Log.Count > 0 && Log[^1].Kind == LogKind.Progress)
                Log[^1].Text = o.Text;
            else
                AddLog(new LogLine(o.Text, kind));
        }
    }

    private void AddLog(LogLine line)
    {
        // Trim in blocks so the list is not shifted on every new line once it is full.
        if (Log.Count >= MaxLogLines)
        {
            var keep = Log.Skip(MaxLogLines / 10).ToList();
            Log.Clear();
            foreach (var l in keep) Log.Add(l);
        }

        Log.Add(line);
    }

    private void Cancel()
    {
        if (_cts == null) return;
        StatusText = "Cancelling …";
        _cts.Cancel();
    }

    private async Task SendInputAsync()
    {
        var text = InputText;
        InputText = "";
        AddLog(new LogLine("< " + text, LogKind.Command));
        await _runner.SendInputAsync(text);
    }

    /// <summary>Runs a helper command (jobs list, login status, env, …) from the toolbar.</summary>
    private async void QuickRun(string? commandId)
    {
        if (string.IsNullOrEmpty(commandId) || IsRunning) return;
        var exe = AzCopyLocator.Find(_settings.AzCopyPath);
        if (exe == null)
        {
            MessageBox.Show("azcopy.exe was not found.", "CopyPilot", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var plan = TransferPlanner.Plan(new CommandRequest { Command = CommandCatalog.Get(commandId) });
        await RunPlanAsync(exe, plan);
    }

    // ---- Profiles ---------------------------------------------------------------------------

    public Profile CreateProfile() => new()
    {
        CommandId = SelectedCommand.Id,
        Source = Source.Spec.Clone(),
        Destination = Destination.Spec.Clone(),
        Target = Target.Spec.Clone(),
        JobId = JobId,
        Options = AllOptions.Where(o => o.IsSet).ToDictionary(o => o.Name, o => o.Value, StringComparer.OrdinalIgnoreCase),
        Environment = EnvironmentVariables.Where(o => o.IsSet).ToDictionary(o => o.Name, o => o.Value, StringComparer.OrdinalIgnoreCase),
        ExtraArguments = ExtraArguments,
    };

    public void ApplyProfile(Profile profile)
    {
        _suspendRefresh = true;
        try
        {
            SelectedCommand = CommandCatalog.All.FirstOrDefault(c => c.Id == profile.CommandId) ?? CommandCatalog.Get(CommandCatalog.Copy);
            Source.Spec = profile.Source;
            Destination.Spec = profile.Destination;
            Target.Spec = profile.Target;
            JobId = profile.JobId;
            foreach (var o in AllOptions) o.Value = profile.Options.GetValueOrDefault(o.Name, "");
            foreach (var e in EnvironmentVariables) e.Value = profile.Environment.GetValueOrDefault(e.Name, "");
            ExtraArguments = profile.ExtraArguments;
        }
        finally
        {
            _suspendRefresh = false;
        }

        OptionsView.Refresh();
        Refresh();
    }

    public void SaveLastSession() => AppSettings.SaveLastSession(CreateProfile());

    private void SaveProfile()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save CopyPilot profile",
            Filter = "CopyPilot profile (*.copypilot.json)|*.copypilot.json|JSON (*.json)|*.json",
            FileName = "transfer.copypilot.json",
        };
        if (dlg.ShowDialog() != true) return;

        var includeSecrets = MessageBox.Show(
            "Include secrets (SAS tokens, passwords, keys) in the profile file?\n\nChoose 'No' unless the file is stored securely.",
            "CopyPilot", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        File.WriteAllText(dlg.FileName, CreateProfile().ToJson(includeSecrets));
        StatusText = "Profile saved to " + dlg.FileName;
    }

    private void LoadProfile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open CopyPilot profile",
            Filter = "CopyPilot profile (*.copypilot.json;*.json)|*.copypilot.json;*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ApplyProfile(Profile.FromJson(File.ReadAllText(dlg.FileName)));
            StatusText = "Profile loaded from " + dlg.FileName;
            SelectedTabIndex = TabTransfer;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            MessageBox.Show("Could not load the profile: " + ex.Message, "CopyPilot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NewProfile()
    {
        if (MessageBox.Show("Start over? All fields, options and environment variables will be cleared.", "CopyPilot",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        ApplyProfile(new Profile());
        SelectedTabIndex = TabTransfer;
    }

    // ---- Misc commands ----------------------------------------------------------------------

    private void ForEachOption(IEnumerable<OptionViewModel> options, Action<OptionViewModel> action)
    {
        _suspendRefresh = true;
        try
        {
            foreach (var o in options) action(o);
        }
        finally
        {
            _suspendRefresh = false;
        }

        OptionsView.Refresh();
        Refresh();
    }

    private void SwapLocations()
    {
        var s = Source.Spec;
        Source.Spec = Destination.Spec;
        Destination.Spec = s;
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText = "Copied to the clipboard.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusText = "The clipboard is busy, try again.";
        }
    }

    private void SaveScript()
    {
        var plan = BuildPlan();
        if (plan.AzCopy is not { } azPlan)
        {
            MessageBox.Show("Transfers that involve SFTP are carried out by CopyPilot itself and cannot be exported as an azcopy script.\n\nSave a profile instead (Save profile…).",
                "CopyPilot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Export as script",
            Filter = "PowerShell script (*.ps1)|*.ps1|Batch file (*.cmd)|*.cmd",
            FileName = "copypilot-" + SelectedCommand.Id.Replace(' ', '-'),
        };
        if (dlg.ShowDialog() != true) return;

        var exe = ResolvedOrDefaultExe;
        var env = BuildEnvironment();
        var script = dlg.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? ScriptExporter.ToCmd(exe, azPlan, env)
            : ScriptExporter.ToPowerShell(exe, azPlan, env);
        File.WriteAllText(dlg.FileName, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        StatusText = "Script saved to " + dlg.FileName + " (contains secrets if any were entered).";
    }

    private void SaveLog()
    {
        var dlg = new SaveFileDialog { Title = "Save output", Filter = "Text file (*.txt)|*.txt", FileName = "copypilot-output.txt" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllLines(dlg.FileName, Log.Select(l => l.Text));
    }

    private void OpenLogFolder()
    {
        var folder = EnvironmentVariables.FirstOrDefault(e => e.Name == "AZCOPY_LOG_LOCATION" && e.IsSet)?.Value
                     ?? Environment.GetEnvironmentVariable("AZCOPY_LOG_LOCATION")
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".azcopy");
        if (!Directory.Exists(folder))
        {
            MessageBox.Show($"The azcopy log folder does not exist yet:\n{folder}", "CopyPilot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void BrowseAzCopy()
    {
        var dlg = new OpenFileDialog { Title = "Select azcopy.exe", Filter = "azcopy.exe|azcopy.exe|Executables (*.exe)|*.exe" };
        if (dlg.ShowDialog() == true) AzCopyPathOverride = dlg.FileName;
    }

    private async Task UpdateAzCopyAsync()
    {
        if (MessageBox.Show(
                $"Download the latest azcopy v10 from Microsoft?\n\n{AzCopyLocator.LatestDownloadUrl}\n\nIt is stored in {AzCopyLocator.UserAzCopyFolder} and used instead of the bundled version.",
                "CopyPilot", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        IsRunning = true;
        IsIndeterminate = true;
        try
        {
            var progress = new Progress<string>(s => StatusText = s);
            await AzCopyLocator.DownloadLatestAsync(progress, CancellationToken.None);
            ResolveAzCopy();
            StatusText = "azcopy updated.";
        }
        catch (Exception ex)
        {
            StatusText = "Update failed: " + ex.Message;
            MessageBox.Show(StatusText, "CopyPilot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsIndeterminate = false;
            IsRunning = false;
        }
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Asks for confirmation when a transfer is running. Returns false to keep the window open.</summary>
    public bool ConfirmClose()
    {
        if (IsRunning)
        {
            if (MessageBox.Show("A transfer is still running. Stop it and exit?", "CopyPilot",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;
            _cts?.Cancel();
        }

        SaveLastSession();
        return true;
    }
}
