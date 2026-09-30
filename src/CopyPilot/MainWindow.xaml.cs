using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CopyPilot.ViewModels;

namespace CopyPilot;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _scrollPending;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Keep the output scrolled to the newest line. Never scroll inside the CollectionChanged
        // notification itself: the ListBox may not have processed the change yet, which makes WPF
        // throw "An ItemsControl is inconsistent with its items source". Scroll once, after layout.
        ((INotifyCollectionChanged)_vm.Log).CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add || _scrollPending) return;
            _scrollPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _scrollPending = false;
                if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
            });
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_vm.ConfirmClose()) e.Cancel = true;
        base.OnClosing(e);
    }
}
