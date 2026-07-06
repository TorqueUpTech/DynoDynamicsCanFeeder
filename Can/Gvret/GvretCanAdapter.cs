using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using IxxatCanTool.Can.Obdx; // reuse the OBDX serial/TCP byte-pipe transports

namespace IxxatCanTool.Can.Gvret;

/// <summary>
/// <see cref="ICanAdapter"/> for SavvyCAN-compatible <b>GVRET</b> loggers (ESP32RET, Macchina A0,
/// CANDUE, ESP32-based "SavvyCAN" boards) over a USB serial port or a TCP socket — the same binary
/// protocol SavvyCAN itself speaks.
///
/// On connect it opens the pipe, enables bus 0 at the chosen bit rate via <c>SET_CANBUS_PARAMS</c>
/// (with the listen-only flag when asked), and starts a background reader that parses
/// <c>BUILD_CAN_FRAME</c> messages. A 1 s keep-alive keeps firmware that expects it streaming. TX
/// builds a <c>BUILD_CAN_FRAME</c> to the device; cyclic TX uses a software timer over Send.
///
/// The device <see cref="CanDeviceInfo.Key"/> is a transport key: <c>serial:COM5</c>
/// (optionally <c>serial:COM5:1000000</c> to set the line rate) or <c>tcp:192.168.4.1:23</c>.
/// </summary>
public sealed class GvretCanAdapter : ICanAdapter
{
    // ---- GVRET binary protocol ----
    private const byte GVRET_START = 0xF1;
    private const byte CMD_BUILD_CAN_FRAME = 0x00;
    private const byte CMD_TIME_SYNC = 0x01;
    private const byte CMD_SET_CANBUS_PARAMS = 0x05;
    private const byte CMD_KEEPALIVE = 0x09;

    // Bus-enable flags packed into the 32-bit speed word of SET_CANBUS_PARAMS.
    private const uint BUS_ENABLED = 0x80000000;
    private const uint BUS_LISTEN_ONLY = 0x40000000;

    // USB-CDC ignores the line rate, but a GVRET board over a real UART commonly runs 1 Mbit.
    private const int DefaultSerialBaud = 1_000_000;

    private readonly object _writeLock = new();
    private readonly object _cyclicLock = new();
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<int, Timer> _cyclic = new();
    private int _nextCyclicHandle;

    private IObdxTransport? _transport;
    private Thread? _rxThread;
    private Timer? _keepAlive;
    private volatile bool _running;

    public event Action<CanFrame>? FrameReceived;
    public event Action<string>? BusError;

    public bool IsConnected { get; private set; }
    public bool SupportsScheduler => false;

    /// <summary>
    /// List candidate serial ports as GVRET devices. A GVRET board is indistinguishable from any
    /// other USB serial device without probing, so every COM port is offered; TCP endpoints are
    /// addressable-only, so type <c>tcp:host:port</c> directly in the device box.
    /// </summary>
    public static IReadOnlyList<CanDeviceInfo> EnumerateDevices()
    {
        var list = new List<CanDeviceInfo>();
        foreach (string port in SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase)
                                          .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            list.Add(new CanDeviceInfo(CanAdapterKind.Gvret, $"serial:{port}", $"GVRET ({port})", "serial"));
        return list;
    }

    public void Connect(CanDeviceInfo device, CanBitRate bitRate, bool listenOnly = false)
    {
        if (IsConnected)
            throw new InvalidOperationException("Already connected.");
        if (device.Adapter != CanAdapterKind.Gvret)
            throw new ArgumentException($"Not a GVRET device: {device.Adapter}.", nameof(device));

        _transport = ResolveTransport(device.Key);
        try
        {
            _transport.Open();
            DrainInput();
            SendSetup(BaudOf(bitRate), listenOnly);

            _clock.Restart();
            _running = true;
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "GVRET-Rx" };
            _rxThread.Start();

            // Some firmware stops streaming without a periodic keep-alive.
            _keepAlive = new Timer(_ => TrySendKeepAlive(), null, 1000, 1000);

            IsConnected = true;
        }
        catch
        {
            CleanupTransport();
            throw;
        }
    }

    /// <summary>serial:COMx[:baud] | tcp:host:port | bare COMx -> a byte pipe to the device.</summary>
    private static IObdxTransport ResolveTransport(string key)
    {
        string k = key.Trim();
        if (k.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            // tcp:host:port
            string rest = k.Substring(4);
            int colon = rest.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(rest.Substring(colon + 1), out int port))
                throw new ArgumentException($"Bad GVRET TCP key '{key}'. Use tcp:host:port.");
            return new TcpObdxTransport(rest.Substring(0, colon), port);
        }

        string spec = k.StartsWith("serial:", StringComparison.OrdinalIgnoreCase) ? k.Substring(7) : k;
        // Optional trailing :baud  (e.g. COM5:1000000)
        int baud = DefaultSerialBaud;
        int bi = spec.IndexOf(':');
        if (bi > 0)
        {
            if (int.TryParse(spec.Substring(bi + 1), out int b)) baud = b;
            spec = spec.Substring(0, bi);
        }
        if (string.IsNullOrWhiteSpace(spec))
            throw new ArgumentException($"Bad GVRET serial key '{key}'. Use serial:COMx.");
        return new SerialObdxTransport(spec, baud);
    }

    /// <summary>Discard any banner/boot bytes so the parser starts clean.</summary>
    private void DrainInput()
    {
        var scratch = new byte[256];
        for (int i = 0; i < 8 && _transport!.Read(scratch) > 0; i++) { /* drop */ }
    }

    /// <summary>Enable bus 0 at <paramref name="baud"/> (bus 1 disabled); set listen-only if asked.</summary>
    private void SendSetup(uint baud, bool listenOnly)
    {
        uint can0 = baud | BUS_ENABLED | (listenOnly ? BUS_LISTEN_ONLY : 0u);
        var msg = new byte[2 + 4 + 4];
        msg[0] = GVRET_START;
        msg[1] = CMD_SET_CANBUS_PARAMS;
        WriteLE(msg, 2, can0); // Can0 speed + flags
        WriteLE(msg, 6, 0);    // Can1 disabled
        lock (_writeLock) _transport!.Write(msg);
    }

    private void TrySendKeepAlive()
    {
        try
        {
            if (_running)
                lock (_writeLock) _transport?.Write(new byte[] { GVRET_START, CMD_KEEPALIVE });
        }
        catch { /* link hiccup; RX loop will surface a hard failure */ }
    }

    public void Send(uint identifier, bool extended, byte[] data, bool remote = false)
    {
        if (!IsConnected || _transport is null)
            throw new InvalidOperationException("Not connected.");
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));

        // BUILD_CAN_FRAME (PC->device): F1 00, id(4 LE, bit31=ext), bus, len, data..., checksum(0)
        uint id = identifier & CanFrame.IdentifierMask;
        if (extended) id |= 0x80000000;
        var msg = new byte[2 + 4 + 1 + 1 + data.Length + 1];
        int p = 0;
        msg[p++] = GVRET_START;
        msg[p++] = CMD_BUILD_CAN_FRAME;
        WriteLE(msg, p, id); p += 4;
        msg[p++] = 0;                    // bus 0
        msg[p++] = (byte)data.Length;    // DLC
        Array.Copy(data, 0, msg, p, data.Length); p += data.Length;
        msg[p] = 0;                      // checksum (ignored by firmware)

        lock (_writeLock) _transport.Write(msg);

        // GVRET does not echo our transmits, so mirror TX into the trace like the other backends.
        FrameReceived?.Invoke(new CanFrame
        {
            TimeStamp = _clock.Elapsed.TotalSeconds,
            Direction = CanDirection.Tx,
            Identifier = identifier,
            IsExtended = extended,
            IsRemote = remote,
            Data = (byte[])data.Clone()
        });
    }

    // ---- RX: byte-fed state machine over the GVRET stream ----

    private enum St { Start, Cmd, FrTs, FrId, FrLen, FrData, FrChk, TimeSync, KeepAlive }

    private void ReceiveLoop()
    {
        var buf = new byte[1024];
        var st = St.Start;
        var ts = new byte[4];
        var idb = new byte[4];
        int need = 0, got = 0, dlc = 0, bus = 0;
        bool ext = false;
        var payload = new byte[8];

        try
        {
            while (_running)
            {
                int n = _transport!.Read(buf);
                if (n <= 0) continue;

                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    switch (st)
                    {
                        case St.Start:
                            if (b == GVRET_START) st = St.Cmd;
                            break;

                        case St.Cmd:
                            switch (b)
                            {
                                case CMD_BUILD_CAN_FRAME: st = St.FrTs; got = 0; break;
                                case CMD_TIME_SYNC: st = St.TimeSync; got = 0; break;
                                case CMD_KEEPALIVE: st = St.KeepAlive; got = 0; break;
                                default: st = St.Start; break; // unknown response -> resync
                            }
                            break;

                        case St.FrTs: // 4-byte micros timestamp (unused; we stamp locally)
                            if (++got == 4) { st = St.FrId; got = 0; }
                            break;

                        case St.FrId:
                            idb[got] = b;
                            if (++got == 4)
                            {
                                uint id = (uint)(idb[0] | (idb[1] << 8) | (idb[2] << 16) | (idb[3] << 24));
                                ext = (id & 0x80000000) != 0;
                                _pendingId = id & CanFrame.IdentifierMask;
                                st = St.FrLen; got = 0;
                            }
                            break;

                        case St.FrLen: // low nibble = DLC, high nibble = bus number
                            dlc = b & 0x0F;
                            bus = (b >> 4) & 0x0F;
                            if (dlc > 8) dlc = 8;
                            need = dlc; got = 0;
                            st = dlc > 0 ? St.FrData : St.FrChk;
                            break;

                        case St.FrData:
                            payload[got] = b;
                            if (++got == need) st = St.FrChk;
                            break;

                        case St.FrChk: // trailing checksum byte (ignored) -> emit the frame
                            var data = new byte[dlc];
                            Array.Copy(payload, data, dlc);
                            FrameReceived?.Invoke(new CanFrame
                            {
                                TimeStamp = _clock.Elapsed.TotalSeconds,
                                Direction = CanDirection.Rx,
                                Identifier = _pendingId,
                                IsExtended = ext,
                                Data = data
                            });
                            _ = bus; // bus number available if multi-bus support is ever needed
                            st = St.Start;
                            break;

                        case St.TimeSync: // F1 01 <4 bytes> — consume and ignore
                            if (++got == 4) st = St.Start;
                            break;

                        case St.KeepAlive: // F1 09 <DE AD> — consume 2 bytes and ignore
                            if (++got == 2) st = St.Start;
                            break;
                    }
                }
            }
        }
        catch (Exception ex) when (_running)
        {
            BusError?.Invoke("GVRET RX stopped: " + ex.Message);
        }
    }

    private uint _pendingId;

    // ---- Cyclic transmit (software timer over Send) ----

    public int StartCyclic(uint identifier, bool extended, byte[] data, bool remote, double intervalMs)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected.");
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));

        byte[] payload = (byte[])data.Clone();
        int period = (int)Math.Max(1, Math.Round(intervalMs));
        lock (_cyclicLock)
        {
            int handle = _nextCyclicHandle++;
            _cyclic[handle] = new Timer(_ => CyclicTick(handle, identifier, extended, payload, remote), null, 0, period);
            return handle;
        }
    }

    private void CyclicTick(int handle, uint id, bool extended, byte[] data, bool remote)
    {
        try { Send(id, extended, data, remote); }
        catch (Exception ex)
        {
            StopCyclic(handle);
            BusError?.Invoke("Cyclic transmit stopped: " + ex.Message);
        }
    }

    public void StopCyclic(int handle)
    {
        lock (_cyclicLock)
            if (_cyclic.TryGetValue(handle, out Timer? timer)) // net48: no Remove(key, out)
            {
                _cyclic.Remove(handle);
                timer.Dispose();
            }
    }

    public void StopAllCyclic()
    {
        lock (_cyclicLock)
        {
            foreach (Timer timer in _cyclic.Values) timer.Dispose();
            _cyclic.Clear();
        }
    }

    public void Disconnect()
    {
        _running = false;
        StopAllCyclic();

        _keepAlive?.Dispose();
        _keepAlive = null;

        if (_rxThread is { } t && t.IsAlive && t != Thread.CurrentThread)
            t.Join(TimeSpan.FromSeconds(2));
        _rxThread = null;

        CleanupTransport();
        IsConnected = false;
    }

    private void CleanupTransport()
    {
        if (_transport is not null)
        {
            try { _transport.Close(); } catch { /* best effort */ }
            _transport.Dispose();
            _transport = null;
        }
    }

    private static void WriteLE(byte[] buf, int offset, uint value)
    {
        buf[offset]     = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static uint BaudOf(CanBitRate br) => br switch
    {
        CanBitRate.Br10kBit => 10000,
        CanBitRate.Br20kBit => 20000,
        CanBitRate.Br50kBit => 50000,
        CanBitRate.Br100kBit => 100000,
        CanBitRate.Br125kBit => 125000,
        CanBitRate.Br250kBit => 250000,
        CanBitRate.Br500kBit => 500000,
        CanBitRate.Br800kBit => 800000,
        CanBitRate.Br1000kBit => 1000000,
        _ => 500000
    };

    public void Dispose() => Disconnect();
}
