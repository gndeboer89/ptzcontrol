using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using PtzJoystickControl.Application.Devices;
using PtzJoystickControl.Core.Devices;
using PtzJoystickControl.Core.Discovery;
using PtzJoystickControl.Core.Services;
using ReactiveUI;

namespace PtzJoystickControl.Gui.ViewModels;

public class DiscoveredCameraItem : ViewModelBase
{
    private bool _isSelected;

    public DiscoveredCameraItem(DiscoveredCamera camera, bool alreadyAdded)
    {
        Camera = camera;
        AlreadyAdded = alreadyAdded;
        _isSelected = !alreadyAdded && camera.SupportsVisca;
    }

    public DiscoveredCamera Camera { get; }
    public bool AlreadyAdded { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    /// <summary>Only cameras we know how to control can be added.</summary>
    public bool CanAdd => !AlreadyAdded && Camera.SupportsVisca;

    public string Title => Camera.DisplayName;

    public string Address => Camera.Address.ToString();

    public string Details
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (Camera.Hardware != null && Camera.Hardware != Camera.Name)
                parts.Add(Camera.Hardware);
            if (Camera.SupportsVisca)
            {
                var kind = Camera.Source.HasFlag(DiscoverySource.ViscaScan) ? "VISCA" : "VISCA? (port open)";
                parts.Add($"{kind} {Camera.ViscaProtocol?.ToString().ToUpperInvariant()} {Camera.ViscaPort}");
            }
            else
                parts.Add("No VISCA port found - cannot be controlled");
            if (Camera.Source.HasFlag(DiscoverySource.Onvif))
                parts.Add("ONVIF");
            if (AlreadyAdded)
                parts.Add("already added");
            return string.Join(" · ", parts);
        }
    }
}

public class DiscoveryViewModel : ViewModelBase
{
    private readonly ICameraDiscoveryService _discovery;
    private readonly ICamerasService _camerasService;
    private CancellationTokenSource? _cts;
    private bool _isScanning;
    private string _status = "Press Scan to look for cameras on your network.";

    public DiscoveryViewModel(ICameraDiscoveryService discovery, ICamerasService camerasService)
    {
        _discovery = discovery;
        _camerasService = camerasService;

        var notScanning = this.WhenAnyValue(x => x.IsScanning, scanning => !scanning);
        ScanCommand = ReactiveCommand.CreateFromTask(ScanAsync, notScanning);
        AddSelectedCommand = ReactiveCommand.Create(AddSelected, notScanning);
    }

    public ObservableCollection<DiscoveredCameraItem> Results { get; } = new();

    public ReactiveCommand<Unit, Unit> ScanCommand { get; }
    public ReactiveCommand<Unit, Unit> AddSelectedCommand { get; }

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

    public void CancelScan() => _cts?.Cancel();

    private async Task ScanAsync()
    {
        _cts = new CancellationTokenSource();
        Results.Clear();
        IsScanning = true;
        Status = "Scanning your network...";

        // Progress<T> posts back to the UI thread we were started on.
        var progress = new Progress<DiscoveredCamera>(OnCameraFound);
        try
        {
            var all = await Task.Run(() => _discovery.DiscoverAsync(progress, _cts.Token));
            Status = _cts.IsCancellationRequested
                ? "Scan cancelled."
                : all.Count == 0
                    ? "No cameras found. Check that the camera is powered on and on the same network, then scan again."
                    : $"Found {all.Count} device(s).";
        }
        catch (Exception e)
        {
            Status = $"Scan failed: {e.Message}";
        }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void OnCameraFound(DiscoveredCamera camera)
    {
        // Results arrive merged per address, so replace an earlier entry for the same address.
        var existing = Results.FirstOrDefault(r => r.Camera.Address.Equals(camera.Address));
        var item = new DiscoveredCameraItem(camera, IsAlreadyAdded(camera.Address));
        if (existing != null)
        {
            item.IsSelected = existing.IsSelected && item.CanAdd;
            Results[Results.IndexOf(existing)] = item;
        }
        else
            Results.Add(item);
    }

    private bool IsAlreadyAdded(IPAddress address) =>
        _camerasService.Cameras.OfType<ViscaIPDeviceBase>().Any(c => c.IPAddress == address.ToString());

    private void AddSelected()
    {
        int added = 0;
        foreach (var item in Results.Where(r => r.IsSelected && r.CanAdd).ToList())
        {
            var camera = item.Camera;
            var device = new ViscaIpDevice(camera.DisplayName)
            {
                IPAddress = camera.Address.ToString(),
                Port = camera.ViscaPort!.Value,
                Protocol = camera.ViscaProtocol ?? Protocol.Udp,
                UseHeader = camera.ViscaUseHeader ?? true,
            };
            _camerasService.AddCamera(device);
            // Replace the row so it shows as added and cannot be added twice.
            Results[Results.IndexOf(item)] = new DiscoveredCameraItem(camera, alreadyAdded: true);
            added++;
        }
        Status = added == 0 ? "Nothing selected." : $"Added {added} camera(s).";
    }
}
