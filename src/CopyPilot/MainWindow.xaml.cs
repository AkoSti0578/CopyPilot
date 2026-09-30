using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using CopyPilot.ViewModels;

namespace CopyPilot;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Keep the output scrolled to the newest line.
        ((INotifyCollectionChanged)_vm.Log).CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace && _vm.Log.Count > 0)
                LogList.ScrollIntoView(_vm.Log[^1]);
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_vm.ConfirmClose()) e.Cancel = true;
        base.OnClosing(e);
    }
}
