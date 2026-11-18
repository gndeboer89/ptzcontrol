using Avalonia.Controls;
using PtzJoystickControl.Gui.ViewModels;

namespace PtzJoystickControl.Gui.Views;

/// <summary>Shows a camera's video, whether it comes from LibVLC (RTSP) or NDI.</summary>
public partial class VideoSurface : UserControl
{
    private VideoViewModel? _viewModel;

    public VideoSurface()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as VideoViewModel);
    }

    private void Attach(VideoViewModel? viewModel)
    {
        if (_viewModel != null)
            _viewModel.FrameUpdated -= OnFrameUpdated;
        _viewModel = viewModel;
        if (_viewModel != null)
            _viewModel.FrameUpdated += OnFrameUpdated;
    }

    // Writing into a WriteableBitmap does not redraw the Image by itself.
    private void OnFrameUpdated() => NdiImage.InvalidateVisual();
}
