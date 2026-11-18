using Avalonia.Controls;
using Avalonia.Interactivity;
using PtzJoystickControl.Gui.ViewModels;

namespace PtzJoystickControl.Gui.Views;

public partial class DiscoveryWindow : Window
{
    public DiscoveryWindow()
    {
        InitializeComponent();
    }

    private void OnStopClicked(object? sender, RoutedEventArgs e) =>
        (DataContext as DiscoveryViewModel)?.CancelScan();

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Do not leave a scan running in the background after the window is gone.
        (DataContext as DiscoveryViewModel)?.CancelScan();
        base.OnClosing(e);
    }
}
