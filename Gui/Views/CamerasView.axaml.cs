using System;
using System.Linq;
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

            try
            {
                CrashLog.Info("Multiview: opening");
                var multiview = viewModel.CreateMultiviewViewModel();
                CrashLog.Info($"Multiview: {multiview.Tiles.Count} tile(s): " +
                              string.Join(", ", multiview.Tiles.Select(t => t.Kind.ToString())));

                var window = new MultiviewWindow { DataContext = multiview };
                window.Opened += (_, _) => CrashLog.Info($"Multiview: window opened at {window.Position} size {window.Bounds.Width:0}x{window.Bounds.Height:0}");
                window.Closed += (_, _) => CrashLog.Info("Multiview: window closed");

                // Not modal, so the camera list and joystick settings stay usable next to it.
                if (TopLevel.GetTopLevel(this) is Window owner)
                    window.Show(owner);
                else
                    window.Show();
                CrashLog.Info("Multiview: Show returned");
            }
            catch (Exception ex)
            {
                CrashLog.Write("Opening the multiview failed", ex);
                MessageWindow.ShowError(this, "The multiview could not be opened", ex);
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
