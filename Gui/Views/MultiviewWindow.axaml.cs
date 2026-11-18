using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using PtzJoystickControl.Gui.ViewModels;

namespace PtzJoystickControl.Gui.Views;

public partial class MultiviewWindow : Window
{
    public MultiviewWindow()
    {
        InitializeComponent();
    }

    private void OnTileTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: VideoViewModel tile } && DataContext is MultiviewViewModel viewModel)
            viewModel.Select(tile);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Stop every stream when the window goes away.
        (DataContext as MultiviewViewModel)?.Dispose();
        base.OnClosed(e);
    }
}

public sealed class ColumnChoiceConverter : IValueConverter
{
    public static readonly ColumnChoiceConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int and > 0 ? value.ToString() : "Auto";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
