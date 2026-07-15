using System.IO;
using System.IO.Ports;
using System.Net.Sockets;

namespace IxxatCanTool.Can.Obdx;

/// <summary>
/// A byte pipe to an OBDX Pro tool (USB virtual COM, WiFi TCP, or — later — BLE). The adapter
/// speaks the DVI protocol over this; the transport only moves bytes. <see cref="Read"/> blocks
/// up to an implementation timeout and returns 0 when nothing arrived (so the RX loop can poll a
/// <c>_running</c> flag), or throws once the link is genuinely closed.
/// </summary>
public interface IObdxTransport : IDisposable
{
    void Open();
    void Write(byte[] data);
    int Read(byte[] buffer);
    void Close();
}

/// <summary>OBDX over a USB virtual COM port.</summary>
public sealed class SerialObdxTransport : IObdxTransport
{
    private readonly string _portName;
    private readonly int _baud;
    private SerialPort? _port;

    // USB CDC ignores the line rate, but SerialPort requires one; 115200 is a safe default.
    public SerialObdxTransport(string portName, int baud = 115200)
    {
        _portName = portName;
        _baud = baud;
    }

    public void Open()
    {
        _port = new SerialPort(_portName, _baud)
        {
            ReadTimeout = 150,
            WriteTimeout = 500
        };
        _port.Open();
    }

    public void Write(byte[] data) => _port!.Write(data, 0, data.Length);

    public int Read(byte[] buffer)
    {
        try { return _port!.Read(buffer, 0, buffer.Length); }
        catch (TimeoutException) { return 0; }
    }

    public void Close()
    {
        try { _port?.Close(); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        Close();
        _port?.Dispose();
        _port = null;
    }
}

/// <summary>OBDX over a WiFi TCP socket.</summary>
public sealed class TcpObdxTransport : IObdxTransport
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpObdxTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public void Open()
    {
        _client = new TcpClient();
        _client.Connect(_host, _port);
        _client.NoDelay = true;
        // Probe a silent peer so a wedged/gone OBDX SoftAP surfaces as a socket error in seconds
        // instead of hanging a half-open connection forever.
        TrySetKeepAlive(_client.Client, timeMs: 2000, intervalMs: 1000);
        _stream = _client.GetStream();
        _stream.ReadTimeout = 150;
    }

    private static void TrySetKeepAlive(Socket sock, uint timeMs, uint intervalMs)
    {
        try
        {
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            // Windows tcp_keepalive: { onoff, keepalivetime(ms), keepaliveinterval(ms) } as u32 LE.
            var ka = new byte[12];
            BitConverter.GetBytes(1u).CopyTo(ka, 0);
            BitConverter.GetBytes(timeMs).CopyTo(ka, 4);
            BitConverter.GetBytes(intervalMs).CopyTo(ka, 8);
            sock.IOControl(IOControlCode.KeepAliveValues, ka, null);
        }
        catch { /* best effort — keep-alive tuning may be unavailable on some stacks */ }
    }

    public void Write(byte[] data) => _stream!.Write(data, 0, data.Length);

    public int Read(byte[] buffer)
    {
        try
        {
            int n = _stream!.Read(buffer, 0, buffer.Length);
            if (n == 0)
                throw new IOException("OBDX connection closed by peer."); // graceful close
            return n;
        }
        catch (IOException ex) when (ex.InnerException is SocketException se
                                     && (se.SocketErrorCode == SocketError.TimedOut
                                         || se.SocketErrorCode == SocketError.WouldBlock))
        {
            return 0; // benign read timeout — nothing arrived this interval
        }
    }

    public void Close()
    {
        try { _stream?.Close(); } catch { /* best effort */ }
        try { _client?.Close(); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        Close();
        _client?.Dispose();
        _client = null;
        _stream = null;
    }
}
