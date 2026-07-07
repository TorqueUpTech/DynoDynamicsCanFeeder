using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace IxxatCanTool.Can.VirtualTcp;

/// <summary>
/// A receive-only "virtual" <see cref="ICanAdapter"/> that connects to a RawCanWire TCP server and
/// turns its 13-byte frames into <see cref="CanFrame"/>s — no physical cable. This lets the feeder
/// be driven from UltimateCANScanner's <c>TcpFrameServer</c> or the CAN-Replay tool (default
/// endpoint <c>127.0.0.1:51729</c>), which is ideal for bench testing the whole DBC -> route ->
/// DynoServer pipeline without any hardware.
///
/// The device <see cref="CanDeviceInfo.Key"/> is a TCP endpoint: <c>tcp:host:port</c>
/// (or bare <c>host:port</c>). Bit rate and listen-only are ignored — a virtual feed has no bus.
/// Transmit is unsupported (the source only replays what the server sends).
/// </summary>
public sealed class VirtualTcpCanAdapter : ICanAdapter
{
    public const string DefaultEndpoint = "tcp:127.0.0.1:51729";

    private readonly Stopwatch _clock = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _rxThread;
    private volatile bool _running;

    public event Action<CanFrame>? FrameReceived;
    public event Action<string>? BusError;

    public bool IsConnected { get; private set; }
    public bool SupportsScheduler => false;

    /// <summary>Not enumerable — offer the default RawCanWire endpoint as a starting point.</summary>
    public static IReadOnlyList<CanDeviceInfo> EnumerateDevices() => new[]
    {
        new CanDeviceInfo(CanAdapterKind.VirtualTcp, DefaultEndpoint,
            "CANScanner / CAN-Replay TCP feed", "127.0.0.1:51729")
    };

    public void Connect(CanDeviceInfo device, CanBitRate bitRate, bool listenOnly = false)
    {
        if (IsConnected)
            throw new InvalidOperationException("Already connected.");
        if (device.Adapter != CanAdapterKind.VirtualTcp)
            throw new ArgumentException($"Not a virtual-TCP device: {device.Adapter}.", nameof(device));

        (string host, int port) = ParseEndpoint(device.Key);
        _client = new TcpClient();
        try
        {
            _client.Connect(host, port);
            _client.NoDelay = true;
            _stream = _client.GetStream();
            _stream.ReadTimeout = 200; // lets the RX loop observe _running between frames

            _clock.Restart();
            _running = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "VirtualTcp-Rx" };
            _rxThread.Start();

            IsConnected = true;
        }
        catch (SocketException ex)
        {
            Cleanup();
            throw new InvalidOperationException(
                $"Could not connect to the virtual CAN source at {host}:{port}. " +
                "Start the TCP frame server in UltimateCANScanner (or CAN-Replay) first. " + ex.Message);
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    private static (string host, int port) ParseEndpoint(string key)
    {
        string k = key.Trim();
        if (k.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            k = k.Substring(4);
        int colon = k.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(k.Substring(colon + 1), out int port))
            throw new ArgumentException($"Bad virtual-TCP key '{key}'. Use tcp:host:port (e.g. {DefaultEndpoint}).");
        return (k.Substring(0, colon), port);
    }

    private void ReceiveLoop()
    {
        var frame = new byte[RawCanWire.FrameSize];
        int filled = 0;
        try
        {
            while (_running)
            {
                int n;
                try { n = _stream!.Read(frame, filled, RawCanWire.FrameSize - filled); }
                catch (IOException) { continue; }   // read timeout — no data; keep any partial frame

                if (n == 0)                          // server closed the connection
                {
                    if (_running) BusError?.Invoke("Virtual TCP source closed the connection.");
                    return;
                }

                filled += n;
                if (filled < RawCanWire.FrameSize)
                    continue;                        // partial frame — wait for the rest

                FrameReceived?.Invoke(RawCanWire.Decode(frame, _clock.Elapsed.TotalSeconds));
                filled = 0;
            }
        }
        catch (Exception ex) when (_running)
        {
            BusError?.Invoke("Virtual TCP RX stopped: " + ex.Message);
        }
    }

    // ---- Transmit: unsupported for a receive-only virtual source ----

    public void Send(uint identifier, bool extended, byte[] data, bool remote = false) =>
        throw new NotSupportedException("The virtual TCP source is receive-only; it cannot transmit.");

    public int StartCyclic(uint identifier, bool extended, byte[] data, bool remote, double intervalMs) =>
        throw new NotSupportedException("The virtual TCP source is receive-only; it cannot transmit.");

    public void StopCyclic(int handle) { /* nothing scheduled */ }
    public void StopAllCyclic() { /* nothing scheduled */ }

    public void Disconnect()
    {
        _running = false;
        if (_rxThread is { } t && t.IsAlive && t != Thread.CurrentThread)
            t.Join(TimeSpan.FromSeconds(2));
        _rxThread = null;
        Cleanup();
        IsConnected = false;
    }

    private void Cleanup()
    {
        try { _stream?.Close(); } catch { /* best effort */ }
        try { _client?.Close(); } catch { /* best effort */ }
        _stream = null;
        _client = null;
    }

    public void Dispose() => Disconnect();
}
