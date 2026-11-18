using Avalonia.Controls;
using Avalonia.Interactivity;
using PtzJoystickControl.Gui.ViewModels;

namespace PtzJoystickControl.Gui.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DataContext = new AboutViewModel();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
