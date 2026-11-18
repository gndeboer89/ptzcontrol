using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PtzJoystickControl.Gui.ViewModels;

namespace PtzJoystickControl.Gui.Views;

public partial class IPCameraView : UserControl
{
    public IPCameraView()
    {
        InitializeComponent();
    }

    private async void OnVideoClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not IPCameraViewModel camera)
            return;

        var window = new VideoWindow
        {
            Title = $"Video - {camera.Name}",
            DataContext = new VideoViewModel(camera.Name, camera.VideoUrl),
        };

        if (TopLevel.GetTopLevel(this) is Window owner)
            await window.ShowDialog(owner);
        else
            window.Show();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
