using Avalonia.Controls;
using Avalonia.Input;
using PtzJoystickControl.Gui.ViewModels;

namespace PtzJoystickControl.Gui.Views;

/// <summary>One camera's picture with its name and status; clicking it selects that camera for the joystick.</summary>
public partial class MultiviewTile : UserControl
{
    public MultiviewTile()
    {
        InitializeComponent();
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is VideoViewModel tile && TopLevel.GetTopLevel(this)?.DataContext is MultiviewViewModel multiview)
            multiview.Select(tile);
    }
}
