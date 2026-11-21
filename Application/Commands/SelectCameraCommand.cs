using PtzJoystickControl.Core.Commands;
using PtzJoystickControl.Core.Devices;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Application.Devices;

namespace PtzJoystickControl.Application.Commands;

public class SelectCameraCommand : IStaticCommand, INotifyPropertyChanged, INotifyCollectionChanged
{
    public SelectCameraCommand(IGamepad gamepad) : base(gamepad)
    {
        Cameras = gamepad.Cameras ?? new ObservableCollection<ViscaDeviceBase>();
        gamepad.PropertyChanged += Gamepad_PropertyChanged;
    }

    private void Gamepad_PropertyChanged(object? sender, PropertyChangedEventArgs? e)
    {
        if (e?.PropertyName == nameof(Gamepad.Cameras))
            Cameras = Gamepad.Cameras ?? new ObservableCollection<ViscaDeviceBase>();
    }

    // One list per command (not static): every command follows the camera list of its own gamepad.
    private ObservableCollection<ViscaDeviceBase> cameras = null!;

    private ObservableCollection<ViscaDeviceBase> Cameras
    {
        get { return cameras; }
        set
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (cameras != null)
            {
                cameras.CollectionChanged -= OnCamerasChanged;
                foreach (var camera in cameras)
                    camera.PropertyChanged -= OnDeviceChange;
            }

            cameras = value;
            
            cameras.CollectionChanged += OnCamerasChanged;
            foreach (var camera in cameras)
                camera.PropertyChanged += OnDeviceChange;
            NotifyPropertyChanged();
        }
    }

    public void OnCamerasChanged(object? o, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Move && (e.OldItems?.Contains(Gamepad.SelectedCamera) ?? false))
            Gamepad.SelectedCamera = null;

        if (e != null)
        {
            if (e.OldStartingIndex >= 0)
                foreach (ViscaIpDevice device in e.OldItems!) device.PropertyChanged -= OnDeviceChange;

            if (e.NewStartingIndex >= 0)
                foreach (ViscaIpDevice device in e.NewItems!) device.PropertyChanged += OnDeviceChange;
        }

        NotifyCollectionChanged(e!);
    }

    private void OnDeviceChange(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViscaDeviceBase.Name))
            NotifyPropertyChanged(nameof(Options));
    }

    public override string CommandName => "Select camera";

    public override string AxisParameterName => "Camera";

    public override string ButtonParameterName => "Camera";

    /// <summary>Value of the option that selects the next camera in the list, after the last one the first (ascending).</summary>
    public const int NextCameraValue = -1;

    /// <summary>Value of the option that selects the previous camera in the list, before the first one the last (descending).</summary>
    public const int PreviousCameraValue = -2;

    /// <summary>
    /// One option per camera (its value is the camera's position, which other code relies on), followed by the two
    /// options that step through the cameras. They come last so that positions in this list stay equal to camera positions.
    /// </summary>
    public override IEnumerable<CommandValueOption> Options => Cameras
        .Select((val, i) => new CommandValueOption(val.Name, i))
        .Concat(new[]
        {
            new CommandValueOption("Next camera (ascending)", NextCameraValue),
            new CommandValueOption("Previous camera (descending)", PreviousCameraValue),
        });

    /// <summary>
    /// Position of the camera to select when stepping by <paramref name="step"/> (+1 or -1) from <paramref name="currentIndex"/>
    /// (-1 when none is selected). Wraps around at both ends. Returns -1 when there are no cameras.
    /// </summary>
    public static int SteppedIndex(int cameraCount, int currentIndex, int step)
    {
        if (cameraCount <= 0)
            return -1;
        if (currentIndex < 0 || currentIndex >= cameraCount)
            return step >= 0 ? 0 : cameraCount - 1; // nothing selected yet: start at the first, or at the last when going down
        return (((currentIndex + step) % cameraCount) + cameraCount) % cameraCount;
    }

    private void SelectRelative(int step)
    {
        var list = Cameras.ToList();
        var current = Gamepad.SelectedCamera;
        int index = SteppedIndex(list.Count, current == null ? -1 : list.IndexOf(current), step);
        if (index >= 0)
            Gamepad.SelectedCamera = list[index];
    }

    public override void Execute(int value)
    {
        if (value == NextCameraValue)
        {
            SelectRelative(+1);
            return;
        }
        if (value == PreviousCameraValue)
        {
            SelectRelative(-1);
            return;
        }

        Gamepad.SelectedCamera = 0 <= value && value < Cameras.Count() ? Cameras[value] : throw new ArgumentOutOfRangeException(
            $"Value out of range for Camera ObservableCollection. Count is {Cameras.Count()}, value was {value}");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    protected void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected void NotifyCollectionChanged(NotifyCollectionChangedEventArgs collectionChangeEventArgs)
    {
        CollectionChanged?.Invoke(this, collectionChangeEventArgs);
    }
}
