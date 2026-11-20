using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PtzJoystickControl.Gui.Views;

/// <summary>A simple window with selectable text, used to show errors the user can copy into a bug report.</summary>
public partial class MessageWindow : Window
{
    public MessageWindow()
    {
        InitializeComponent();
    }

    public MessageWindow(string title, string text) : this()
    {
        Title = title;
        MessageBox.Text = text;
    }

    /// <summary>Shows an error with its technical details. Never throws.</summary>
    public static void ShowError(Visual? from, string title, Exception exception)
    {
        try
        {
            var text = exception.Message + Environment.NewLine + Environment.NewLine + exception +
                       Environment.NewLine + Environment.NewLine + "Log file: " + CrashLog.FilePath;
            var window = new MessageWindow(title, text);
            if (from != null && TopLevel.GetTopLevel(from) is Window owner)
                window.Show(owner);
            else
                window.Show();
        }
        catch (Exception e)
        {
            CrashLog.Write("Could not show the error window", e);
        }
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (Clipboard != null)
            await Clipboard.SetTextAsync(MessageBox.Text ?? "");
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
