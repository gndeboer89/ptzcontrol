using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PtzJoystickControl.Gui.ViewModels;

namespace PtzJoystickControl.Gui.Views
{
    public partial class CamerasView : UserControl
    {
        public CamerasView()
        {
            InitializeComponent();
        }

        private async void OnFindCamerasClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not CamerasViewModel viewModel)
                return;

            var window = new DiscoveryWindow { DataContext = viewModel.CreateDiscoveryViewModel() };
            if (TopLevel.GetTopLevel(this) is Window owner)
                await window.ShowDialog(owner);
            else
                window.Show();
        }

        private void OnMultiviewClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not CamerasViewModel viewModel)
                return;

            var window = new MultiviewWindow { DataContext = viewModel.CreateMultiviewViewModel() };
            // Not modal, so the camera list and joystick settings stay usable next to it.
            if (TopLevel.GetTopLevel(this) is Window owner)
                window.Show(owner);
            else
                window.Show();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
