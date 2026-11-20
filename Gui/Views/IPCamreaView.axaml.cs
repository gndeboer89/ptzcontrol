using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PtzJoystickControl.Core.Video;
using PtzJoystickControl.Gui.ViewModels;

namespace PtzJoystickControl.Gui.Views;

public partial class IPCameraView : UserControl
{
    public IPCameraView()
    {
        InitializeComponent();
    }

    private async void OnPickNdiClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not IPCameraViewModel camera)
            return;

        var picker = new NdiSourcePickerWindow();
        string? name = null;
        if (TopLevel.GetTopLevel(this) is Window owner)
            name = await picker.ShowDialog<string?>(owner);
        else
            picker.Show();

        if (!string.IsNullOrWhiteSpace(name))
            camera.VideoUrl = VideoSourceUrl.NdiUrl(name);
    }

    private void OnVideoClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not IPCameraViewModel camera)
            return;

        try
        {
            CrashLog.Info($"Video: opening for {camera.Name}");
            var window = new VideoWindow
            {
                Title = $"Video - {camera.Name}",
                DataContext = new VideoViewModel(camera.Name, camera.VideoUrl),
            };

            // Not modal: the main window stays usable (and several videos can be open at once).
            if (TopLevel.GetTopLevel(this) is Window owner)
                window.Show(owner);
            else
                window.Show();
            CrashLog.Info("Video: Show returned");
        }
        catch (System.Exception ex)
        {
            CrashLog.Write("Opening the video window failed", ex);
            MessageWindow.ShowError(this, "The video could not be opened", ex);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
