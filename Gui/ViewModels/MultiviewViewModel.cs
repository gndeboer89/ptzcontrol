using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using PtzJoystickControl.Core.Devices;
using ReactiveUI;

namespace PtzJoystickControl.Gui.ViewModels;

/// <summary>Several cameras' video side by side in one grid, with the camera under joystick control highlighted.</summary>
public class MultiviewViewModel : ViewModelBase, IDisposable
{
    /// <summary>Name of the property that announces a change of the controlled camera.</summary>
    public const string SelectedCameraProperty = "SelectedCamera";

    private readonly Action<ViscaDeviceBase> _selectCamera;
    private readonly Func<ViscaDeviceBase?> _getSelectedCamera;
    private readonly INotifyPropertyChanged? _selectionSource;
    private int _columnChoice;
    private string _activeDescription = "";
    private bool _focusLayout = true;
    private VideoViewModel? _activeTile;
    private IReadOnlyList<VideoViewModel> _otherTiles = Array.Empty<VideoViewModel>();

    /// <param name="cameras">All cameras; only those with a video address get a tile.</param>
    /// <param name="selectCamera">Called when the user clicks a tile.</param>
    /// <param name="getSelectedCamera">The camera the joystick controls now (null when none).</param>
    /// <param name="selectionSource">Raises PropertyChanged("SelectedCamera") whenever that changes, also from a controller button.</param>
    public MultiviewViewModel(
        IEnumerable<ViscaIPDeviceBase> cameras,
        Action<ViscaDeviceBase> selectCamera,
        Func<ViscaDeviceBase?>? getSelectedCamera = null,
        INotifyPropertyChanged? selectionSource = null)
    {
        _selectCamera = selectCamera;
        _getSelectedCamera = getSelectedCamera ?? (() => null);
        _selectionSource = selectionSource;

        Tiles = cameras
            .Where(c => !string.IsNullOrWhiteSpace(c.VideoUrl))
            .Select(c => new VideoViewModel(c.Name, c.VideoUrl, c))
            .ToList();

        if (_selectionSource != null)
            _selectionSource.PropertyChanged += OnSelectionChanged;
        RefreshActive();
    }

    public IReadOnlyList<VideoViewModel> Tiles { get; }

    public bool HasTiles => Tiles.Count > 0;

    public string Summary => HasTiles
        ? $"{Tiles.Count} camera(s). Click a picture to select that camera for the joystick."
        : "None of your cameras has a video URL yet. Fill in the video field of a camera (RTSP or NDI) and open the multiview again.";

    /// <summary>For example "Controlling: Stage left", or a hint when no camera is selected.</summary>
    public string ActiveDescription
    {
        get => _activeDescription;
        private set => this.RaiseAndSetIfChanged(ref _activeDescription, value);
    }

    /// <summary>User choice: show the camera under joystick control large and the others small.</summary>
    public bool FocusLayout
    {
        get => _focusLayout;
        set
        {
            this.RaiseAndSetIfChanged(ref _focusLayout, value);
            RaiseLayoutChanged();
        }
    }

    /// <summary>The tile of the camera under joystick control, if it has one.</summary>
    public VideoViewModel? ActiveTile
    {
        get => _activeTile;
        private set => this.RaiseAndSetIfChanged(ref _activeTile, value);
    }

    /// <summary>All other tiles, shown small next to the active one.</summary>
    public IReadOnlyList<VideoViewModel> OtherTiles
    {
        get => _otherTiles;
        private set => this.RaiseAndSetIfChanged(ref _otherTiles, value);
    }

    /// <summary>Large picture plus small ones: needs the user's choice and a camera that is actually selected.</summary>
    public bool IsFocusMode => FocusLayout && ActiveTile != null;

    /// <summary>Equal grid, for when the user prefers it or no camera is selected.</summary>
    public bool IsGridMode => !IsFocusMode;

    private void RaiseLayoutChanged()
    {
        this.RaisePropertyChanged(nameof(IsFocusMode));
        this.RaisePropertyChanged(nameof(IsGridMode));
    }

    /// <summary>0 = automatic, otherwise a fixed number of columns.</summary>
    public IReadOnlyList<int> ColumnChoices { get; } = new[] { 0, 1, 2, 3, 4, 5 };

    public int ColumnChoice
    {
        get => _columnChoice;
        set
        {
            this.RaiseAndSetIfChanged(ref _columnChoice, value);
            this.RaisePropertyChanged(nameof(Columns));
        }
    }

    public int Columns => ComputeColumns(Tiles.Count, _columnChoice);

    public void Select(VideoViewModel tile)
    {
        if (tile.Camera is ViscaDeviceBase camera)
            _selectCamera(camera);
        // The highlight follows the real selection (see OnSelectionChanged), so it stays right even when
        // there is no controller to select a camera for.
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == SelectedCameraProperty || string.IsNullOrEmpty(e.PropertyName))
            RefreshActive();
    }

    /// <summary>Marks the tile of the camera the joystick controls now, and clears the others.</summary>
    public void RefreshActive()
    {
        var selected = _getSelectedCamera();
        foreach (var tile in Tiles)
            tile.IsActive = selected != null && ReferenceEquals(tile.Camera, selected);

        var active = Tiles.FirstOrDefault(t => t.IsActive);
        ActiveTile = active;
        OtherTiles = Tiles.Where(t => !t.IsActive).ToList();
        RaiseLayoutChanged();
        ActiveDescription = active != null
            ? "Controlling: " + active.CameraName
            : selected != null
                ? "Controlling: " + selected.Name + " (no video address)"
                : "No camera selected for the joystick.";
    }

    /// <summary>Automatic layout aims for a roughly square grid.</summary>
    public static int ComputeColumns(int tileCount, int choice)
    {
        if (tileCount <= 0)
            return 1;
        if (choice > 0)
            return Math.Min(choice, tileCount);
        return (int)Math.Ceiling(Math.Sqrt(tileCount));
    }

    public void Dispose()
    {
        if (_selectionSource != null)
            _selectionSource.PropertyChanged -= OnSelectionChanged;
        foreach (var tile in Tiles)
            tile.Dispose();
    }
}
