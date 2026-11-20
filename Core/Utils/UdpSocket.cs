using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PtzJoystickControl.Core.Utils;

public class UdpSocket
{
    private static readonly Dictionary<EndPoint, Action<byte[], int>> receiveCallbacks = new();
    private static readonly object callbacksLock = new(); // cameras start and stop from several threads
    private static Socket? socket;
    private static int port = 0;

    public int Port
    {
        get => port;
        set => port = value;
    }

    private static byte[] _buffer = new byte[128];

    private static readonly object socketGate = new();

    public static Socket GetInstance()
    {
        lock (socketGate)
        {
            if (socket == null)
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    ReceiveBufferSize = 128,
                    SendBufferSize = 128
                };
                DisableConnectionReset(socket);
            }

            if (!socket.IsBound)
            {
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                    ArmReceive(socket);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"UDP GetInstance: {e.Message}");
                }
            }
            return socket;
        }
    }

    /// <summary>
    /// Windows reports an ICMP "port unreachable" for a packet we sent as an error on the next receive of the same socket
    /// (SIO_UDP_CONNRESET). This socket is shared by all cameras, so one camera that is off would otherwise disturb the others.
    /// </summary>
    private static void DisableConnectionReset(Socket udp)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            udp.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch (Exception e) when (e is SocketException or NotSupportedException or InvalidOperationException)
        {
            Debug.WriteLine($"UDP could not disable connection reset: {e.Message}");
        }
    }

    /// <summary>
    /// Receive errors that only say something about an earlier send to one particular camera (it refused or could not be reached).
    /// They must never stop the receive loop, because other cameras share this socket.
    /// </summary>
    public static bool IsHarmlessReceiveError(SocketError error) =>
        error is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.HostUnreachable
            or SocketError.NetworkUnreachable or SocketError.HostDown or SocketError.TimedOut;

    private static void ArmReceive(Socket udp)
    {
        EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
        udp.BeginReceiveFrom(_buffer, 0, _buffer.Length, SocketFlags.None, ref remoteEndPoint, new AsyncCallback(OnRecieve), udp);
    }

    private static void OnRecieve(IAsyncResult res)
    {
        var udp = (Socket)res.AsyncState!;
        int length = 0;
        EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            length = udp.EndReceiveFrom(res, ref remoteEndPoint);
        }
        catch (ObjectDisposedException)
        {
            return; // the socket was closed on purpose
        }
        catch (SocketException e) when (IsHarmlessReceiveError(e.SocketErrorCode))
        {
            Debug.WriteLine($"UDP OnReceive (ignored): {e.SocketErrorCode}");
        }
        catch (SocketException e)
        {
            Debug.WriteLine($"UDP OnReceive: {e.Message}");
            lock (socketGate)
            {
                if (ReferenceEquals(socket, udp))
                    socket = null;
            }
            udp.Dispose();
            return;
        }

        if (length > 0)
        {
            Debug.WriteLine($"UDP OnReceive: {remoteEndPoint} - {BitConverter.ToString(_buffer, 0, length)}");
            Action<byte[], int>? callback = null;
            lock (callbacksLock)
                receiveCallbacks.TryGetValue(remoteEndPoint, out callback);
            if (callback != null)
            {
                try
                {
                    callback(_buffer, length);
                }
                catch (Exception e)
                {
                    // One bad packet must not stop the receive loop for every camera.
                    Debug.WriteLine($"UDP callback failed: {e.Message}");
                }
            }
        }

        try
        {
            lock (socketGate)
            {
                if (ReferenceEquals(socket, udp))
                    ArmReceive(udp);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            Debug.WriteLine($"UDP OnReceive (re-arm): {e.Message}");
        }
    }

    /// <summary>
    /// Add callback to receive UDP packets from remote endpint
    /// </summary>
    /// <param name="remoteEndpint">Remote endpoint to receive packets from</param>
    /// <param name="callback">Callback wich recived buffer and content length as parameters</param>
    public static void AddListenerCallback(EndPoint remoteEndpint, Action<byte[], int> callback)
    {
        // Registering the same endpoint again (a restart racing another restart) simply takes over.
        lock (callbacksLock)
            receiveCallbacks[remoteEndpint] = callback;
    }

    public static void RemoveListenerCallback(EndPoint remoteEndpint)
    {
        lock (callbacksLock)
            receiveCallbacks.Remove(remoteEndpint);
    }
}
