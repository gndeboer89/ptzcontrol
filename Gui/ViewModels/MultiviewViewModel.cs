using System;
using System.Collections.Generic;
using System.Linq;
using PtzJoystickControl.Core.Devices;
using ReactiveUI;

namespace PtzJoystickControl.Gui.ViewModels;

/// <summary>Several cameras' video side by side in one grid.</summary>
public class MultiviewViewModel : ViewModelBase, IDisposable
{
    private readonly Action<ViscaDeviceBase> _selectCamera;
    private int _columnChoice;
    private VideoViewModel? _selectedTile;

    public MultiviewViewModel(IEnumerable<ViscaIPDeviceBase> cameras, Action<ViscaDeviceBase> selectCamera)
    {
        _selectCamera = selectCamera;
        Tiles = cameras
            .Where(c => !string.IsNullOrWhiteSpace(c.VideoUrl))
            .Select(c => new VideoViewModel(c.Name, c.VideoUrl, c))
            .ToList();
    }

    public IReadOnlyList<VideoViewModel> Tiles { get; }

    public bool HasTiles => Tiles.Count > 0;

    public string Summary => HasTiles
        ? $"{Tiles.Count} camera(s). Click a picture to select that camera for the joystick."
        : "None of your cameras has a video URL yet. Fill in the video field of a camera (RTSP or NDI) and open the multiview again.";

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

    public VideoViewModel? SelectedTile
    {
        get => _selectedTile;
        private set => this.RaiseAndSetIfChanged(ref _selectedTile, value);
    }

    public void Select(VideoViewModel tile)
    {
        SelectedTile = tile;
        if (tile.Camera is ViscaDeviceBase camera)
            _selectCamera(camera);
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
        foreach (var tile in Tiles)
            tile.Dispose();
    }
}
