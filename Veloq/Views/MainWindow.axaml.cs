using Avalonia.Controls;
using Avalonia.Threading;

namespace Veloq.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Opacity = 0;
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        Opened -= OnOpened;
        Dispatcher.UIThread.Post(() => Opacity = 1, DispatcherPriority.Loaded);
    }
}
