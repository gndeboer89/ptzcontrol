using Avalonia.Controls;
using PtzJoystickControl.Gui.ViewModels;

namespace PtzJoystickControl.Gui.Views;

public partial class VideoWindow : Window
{
    public VideoWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(System.EventArgs e)
    {
        // Release the stream and native player when the window goes away.
        (DataContext as VideoViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
