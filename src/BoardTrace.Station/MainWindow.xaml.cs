using System.ComponentModel;
using System.Windows;

namespace BoardTrace.Station;

public partial class MainWindow : Window
{
    private bool closing;
    private bool stopped;

    public MainWindow() => InitializeComponent();

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (stopped || DataContext is not StationViewModel model)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        base.OnClosing(e);
        if (closing) return;
        closing = true;
        await model.DisposeAsync();
        stopped = true;
        _ = Dispatcher.InvokeAsync(Close);
    }
}
