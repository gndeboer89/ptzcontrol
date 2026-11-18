using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using PtzJoystickControl.Gui.Video;
using ReactiveUI;

namespace PtzJoystickControl.Gui.ViewModels;

public class NdiSourcePickerViewModel : ViewModelBase
{
    private CancellationTokenSource? _cts;
    private bool _isScanning;
    private string _status = "Looking for NDI sources...";
    private string? _selected;

    public NdiSourcePickerViewModel()
    {
        ScanCommand = ReactiveCommand.CreateFromTask(ScanAsync, this.WhenAnyValue(x => x.IsScanning, scanning => !scanning));
    }

    public ObservableCollection<string> Sources { get; } = new();
    public ReactiveCommand<Unit, Unit> ScanCommand { get; }

    public bool IsScanning
    {
        get => _isScanning;
        private set => this.RaiseAndSetIfChanged(ref _isScanning, value);
    }

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public string? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    public void Cancel() => _cts?.Cancel();

    private async Task ScanAsync()
    {
        _cts = new CancellationTokenSource();
        IsScanning = true;
        Status = "Looking for NDI sources...";
        Sources.Clear();
        try
        {
            var found = await Task.Run(() => NdiSourceFinder.FindAsync(TimeSpan.FromSeconds(4), _cts.Token));
            foreach (var name in found)
                Sources.Add(name);
            Status = found.Count == 0
                ? "No NDI sources found. Check that the camera or app sends NDI on this network, then scan again."
                : $"Found {found.Count} NDI source(s). Select one and press Use.";
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled.";
        }
        catch (InvalidOperationException e)
        {
            Status = e.Message; // NDI runtime missing etc.
        }
        catch (Exception e)
        {
            Status = "Scan failed: " + e.Message;
        }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }
}
