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

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
