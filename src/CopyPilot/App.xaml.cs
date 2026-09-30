using System.Windows;
using System.Windows.Threading;

namespace CopyPilot;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Unexpected error:\n\n" + e.Exception.Message, "CopyPilot", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
