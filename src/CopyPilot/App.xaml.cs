using System.IO;
using System.Windows;
using System.Windows.Threading;
using CopyPilot.Services;

namespace CopyPilot;

public partial class App : Application
{
    private bool _showingError;
    private int _suppressedErrors;

    public static string ErrorLogFile => Path.Combine(AppSettings.Folder, "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        LogError(e.Exception);

        // One dialog at a time: an error that repeats on every layout pass must not flood the screen.
        if (_showingError)
        {
            _suppressedErrors++;
            return;
        }

        _showingError = true;
        try
        {
            MessageBox.Show(
                "Unexpected error:\n\n" + e.Exception.Message + "\n\nDetails were written to:\n" + ErrorLogFile,
                "CopyPilot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
            if (_suppressedErrors > 0)
            {
                LogError(new InvalidOperationException($"{_suppressedErrors} further error(s) suppressed while the dialog was open."));
                _suppressedErrors = 0;
            }
        }
    }

    private static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(ErrorLogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // never fail while reporting a failure
        }
    }
}
