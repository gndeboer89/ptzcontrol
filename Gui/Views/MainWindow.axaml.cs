using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PtzJoystickControl.Gui.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            //var logWin = new LogWindow();
            //logWin.DataContext = new LogWindowViewModel();
            //logWin.Show();
        }

        private async void OnAboutClicked(object? sender, RoutedEventArgs e)
        {
            await new AboutWindow().ShowDialog(this);
        }
    }
}
