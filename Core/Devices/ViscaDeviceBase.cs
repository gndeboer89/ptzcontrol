using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PtzJoystickControl.Core.Devices;

public abstract class ViscaDeviceBase : INotifyPropertyChanged
{
    protected string name = null!;
    protected byte address = 0x01;
    protected int sendWaitTime = 30;
    protected byte[] sendBuffer = new byte[128];
    protected byte[] receiveBuffer = new byte[128];
    protected int sendBuffIndex = 0;
    protected int receiveBuffIndex = 0;
    protected DateTime lastSendTime = DateTime.UtcNow;
    protected DateTime lastReceiveTime = DateTime.MinValue;

    protected byte powerCmd;

    protected byte panSpeed = 0x04;
    protected byte tiltSpeed = 0x04;
    protected PanDir panDir = PanDir.Stop;
    protected TiltDir tiltDir = TiltDir.Stop;

    protected byte zoomCmd = 0x00;

    protected byte focusCmd;
    protected byte focusModeCmd;
    protected byte focusLockCmd;

    protected byte presetCmd;
    protected byte presetCmdNumber;
    protected byte presetRecallSpeed = 0x04;


    public event PropertyChangedEventHandler? PropertyChanged;

    protected void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public event PropertyChangedEventHandler? PersistentPropertyChanged;
    protected void NotifyPersistentPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PersistentPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public ViscaDeviceBase(string name)
    {
        Name = name;
    }
    
    protected internal Action<ViscaDeviceBase, byte[], int>? InquiryReplyParser { get; set; }
    protected internal bool Acked { get; internal set; }
    protected internal bool Completed { get; internal set; }

    public string Name
    {
        get { return name; }
        set
        {
            if (name == value)
                return;

            if(string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Name cannot be empty", nameof(value));

                name = value;
                NotifyPropertyChanged();
                NotifyPersistentPropertyChanged();
            
        }
    }

    public byte ViscaAddress
    {
        get => address;
        set => address = 0 < value && value < 128 
            ? value
            : throw new ArgumentOutOfRangeException($"ViscaAddress must be between 1 and 127. {value} was given.");
    }

    protected DateTime LastSendTime
    {
        get => lastSendTime;
        set => lastSendTime = value;
    }

    protected internal DateTime LastReceiveTime {
        get => lastReceiveTime;
        set
        {
            lastReceiveTime = value;
            PublishConnectionState();
        }
    }

    /// <summary>How long after the last reply from the camera it still counts as connected.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>True while the camera has answered recently (the program asks regularly, so no controller is needed).</summary>
    public virtual bool Connected =>
        lastReceiveTime != DateTime.MinValue && DateTime.UtcNow - lastReceiveTime <= ConnectionTimeout;

    private bool publishedConnected;

    /// <summary>Raises PropertyChanged for <see cref="Connected"/> when its value differs from what was last announced.</summary>
    protected void PublishConnectionState()
    {
        var connected = Connected;
        if (connected == publishedConnected)
            return;
        publishedConnected = connected;
        NotifyPropertyChanged(nameof(Connected));
    }

    public Power? PowerState { get; protected internal set; }

    public abstract void Power(Power byteVal);
    public abstract void Pan(byte panSpeed, PanDir panDir);
    public abstract void Tilt(byte tiltSpeed, TiltDir tiltDir);
    public abstract void PanTilt(byte panSpeed, byte tiltSpeed, PanDir panDir, TiltDir tiltDir);
    public abstract void Zoom(byte zoomSpeed, ZoomDir zoomDir);
    public abstract void Focus(byte focusSpeed, FocusDir focusDir);
    public abstract void FocusMode(FocusMode focusMode);
    public abstract void FocusLock(FocusLock focusMode);
    public abstract void Preset(Preset preset, byte presetNumber);
    public abstract void PresetRecallSpeed(byte value);
}
