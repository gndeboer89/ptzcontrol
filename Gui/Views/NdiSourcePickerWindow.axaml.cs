using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PtzJoystickControl.Gui.ViewModels;

namespace PtzJoystickControl.Gui.Views;

/// <summary>Lets the user pick an NDI source; closes with the chosen name, or null.</summary>
public partial class NdiSourcePickerWindow : Window
{
    public NdiSourcePickerWindow()
    {
        InitializeComponent();
        DataContext = new NdiSourcePickerViewModel();
        Opened += (_, _) => (DataContext as NdiSourcePickerViewModel)?.ScanCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    private void OnUseClicked(object? sender, RoutedEventArgs e) => Close((DataContext as NdiSourcePickerViewModel)?.Selected);

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((DataContext as NdiSourcePickerViewModel)?.Selected is { } name)
            Close(name);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        (DataContext as NdiSourcePickerViewModel)?.Cancel();
        base.OnClosing(e);
    }
}
