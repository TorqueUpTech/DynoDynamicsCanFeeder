using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace IxxatCanTool.Can.Peak;

/// <summary>
/// <see cref="ICanAdapter"/> for PEAK <b>PCAN-USB</b> interfaces via the classic <c>PCANBasic.dll</c>
/// API — the same family DigitalDyno itself uses. Channels PCAN_USBBUS1..16 are probed on
/// enumeration; connect initialises the chosen channel at the requested bit rate (in listen-only
/// mode when asked, so the feeder never ACKs), then a background thread drains the RX queue. TX
/// writes a <c>TPCANMsg</c>; cyclic TX uses a software timer over Send.
///
/// The device <see cref="CanDeviceInfo.Key"/> is the numeric TPCANHandle (e.g. "81" for
/// PCAN_USBBUS1 = 0x51).
/// </summary>
public sealed class PcanCanAdapter : ICanAdapter
{
    private readonly object _writeLock = new();
    private readonly object _cyclicLock = new();
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<int, Timer> _cyclic = new();
    private int _nextCyclicHandle;

    private ushort _channel;
    private Thread? _rxThread;
    private volatile bool _running;
    private bool _busErrorReported;

    public event Action<CanFrame>? FrameReceived;
    public event Action<string>? BusError;

    public bool IsConnected { get; private set; }
    public bool SupportsScheduler => false;

    /// <summary>Probe PCAN-USB channels 1..16 and list the ones the driver reports present.</summary>
    public static IReadOnlyList<CanDeviceInfo> EnumerateDevices()
    {
        var list = new List<CanDeviceInfo>();
        try
        {
            for (int i = 0; i < 16; i++)
            {
                ushort handle = ChannelHandle(i);
                uint status = PcanApi.CAN_GetValue(handle, PcanApi.PCAN_CHANNEL_CONDITION, out uint cond, sizeof(uint));
                if (status != PcanApi.PCAN_ERROR_OK)
                    continue;
                if ((cond & (PcanApi.PCAN_CHANNEL_AVAILABLE | PcanApi.PCAN_CHANNEL_OCCUPIED)) == 0)
                    continue;
                string detail = (cond & PcanApi.PCAN_CHANNEL_OCCUPIED) != 0 ? "in use by another app" : "PCAN-USB";
                list.Add(new CanDeviceInfo(CanAdapterKind.Peak,
                    handle.ToString(CultureInfo.InvariantCulture), $"PCAN-USB {i + 1}", detail));
            }
        }
        catch (DllNotFoundException) { /* PEAK driver not installed — no devices */ }
        catch (BadImageFormatException) { /* wrong-bitness driver — no devices */ }
        return list;
    }

    /// <summary>TPCANHandle for USB channel index 0..15 (1..8 => 0x51..0x58, 9..16 => 0x509..0x510).</summary>
    private static ushort ChannelHandle(int index) =>
        index < 8 ? (ushort)(PcanApi.PCAN_USBBUS1 + index) : (ushort)(PcanApi.PCAN_USBBUS9 + (index - 8));

    public void Connect(CanDeviceInfo device, CanBitRate bitRate, bool listenOnly = false)
    {
        if (IsConnected)
            throw new InvalidOperationException("Already connected.");
        if (device.Adapter != CanAdapterKind.Peak)
            throw new ArgumentException($"Not a PEAK device: {device.Adapter}.", nameof(device));
        if (!ushort.TryParse(device.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _channel))
            throw new ArgumentException($"Bad PEAK channel key '{device.Key}'.", nameof(device));

        uint status;
        try
        {
            status = PcanApi.CAN_Initialize(_channel, BaudOf(bitRate), 0, 0, 0);
        }
        catch (DllNotFoundException)
        {
            throw new InvalidOperationException(
                "PEAK PCANBasic driver not found. Install the PEAK-System device driver package, then retry.");
        }
        if (status != PcanApi.PCAN_ERROR_OK)
            throw new IOException($"PCAN CAN_Initialize failed: {PcanApi.ErrorText(status)}");

        // Listen-only keeps the feeder passive (no ACK). Not all hardware supports it; warn if not.
        if (listenOnly)
        {
            uint on = PcanApi.PCAN_PARAMETER_ON;
            uint lo = PcanApi.CAN_SetValue(_channel, PcanApi.PCAN_LISTEN_ONLY, ref on, sizeof(uint));
            if (lo != PcanApi.PCAN_ERROR_OK)
                BusError?.Invoke($"PCAN: listen-only not supported here ({PcanApi.ErrorText(lo)}); the tool will ACK.");
        }

        _clock.Restart();
        _running = true;
        _busErrorReported = false;
        _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "PCAN-Rx" };
        _rxThread.Start();

        IsConnected = true;
    }

    private void ReceiveLoop()
    {
        try
        {
            while (_running)
            {
                uint status = PcanApi.CAN_Read(_channel, out TPCANMsg msg, out _);

                if (status == PcanApi.PCAN_ERROR_QRCVEMPTY)
                {
                    Thread.Sleep(1); // queue empty — idle briefly so we don't spin a core
                    continue;
                }
                if (status != PcanApi.PCAN_ERROR_OK)
                {
                    // Bus-state warnings (light/heavy/off/overrun) come back here; report once so the
                    // log isn't flooded, and keep reading (the bus may recover).
                    if (!_busErrorReported && _running)
                    {
                        BusError?.Invoke($"PCAN status: {PcanApi.ErrorText(status)}");
                        _busErrorReported = true;
                    }
                    continue;
                }
                _busErrorReported = false;

                // Skip status frames (error/bus-state carriers, not real data).
                if ((msg.MSGTYPE & PcanApi.PCAN_MESSAGE_STATUS) != 0)
                    continue;

                bool ext = (msg.MSGTYPE & PcanApi.PCAN_MESSAGE_EXTENDED) != 0;
                int len = Math.Min((int)msg.LEN, 8);
                var data = new byte[len];
                Array.Copy(msg.DATA, data, len);

                FrameReceived?.Invoke(new CanFrame
                {
                    TimeStamp = _clock.Elapsed.TotalSeconds,
                    Direction = CanDirection.Rx,
                    Identifier = msg.ID & (ext ? CanFrame.IdentifierMask : 0x7FFu),
                    IsExtended = ext,
                    IsRemote = (msg.MSGTYPE & PcanApi.PCAN_MESSAGE_RTR) != 0,
                    Data = data
                });
            }
        }
        catch (Exception ex) when (_running)
        {
            BusError?.Invoke("PCAN RX stopped: " + ex.Message);
        }
    }

    public void Send(uint identifier, bool extended, byte[] data, bool remote = false)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected.");
        if (data.Length > 8)
            throw new ArgumentException("Classic CAN allows at most 8 data bytes.", nameof(data));

        byte type = extended ? PcanApi.PCAN_MESSAGE_EXTENDED : PcanApi.PCAN_MESSAGE_STANDARD;
        if (remote) type |= PcanApi.PCAN_MESSAGE_RTR;

        var msg = new TPCANMsg
        {
            ID = identifier & (extended ? CanFrame.IdentifierMask : 0x7FFu),
            MSGTYPE = type,
            LEN = (byte)data.Length,
            DATA = new byte[8]
        };
        Array.Copy(data, msg.DATA, data.Length);

        uint status;
        lock (_writeLock)
            status = PcanApi.CAN_Write(_channel, ref msg);
        if (status != PcanApi.PCAN_ERROR_OK)
            throw new IOException($"PCAN CAN_Write failed: {PcanApi.ErrorText(status)}");

        // PCANBasic does not self-receive transmits, so mirror TX into the trace like the others.
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

        if (_rxThread is { } t && t.IsAlive && t != Thread.CurrentThread)
            t.Join(TimeSpan.FromSeconds(2));
        _rxThread = null;

        if (_channel != PcanApi.PCAN_NONEBUS)
        {
            try { PcanApi.CAN_Uninitialize(_channel); } catch { /* best effort */ }
            _channel = PcanApi.PCAN_NONEBUS;
        }
        IsConnected = false;
    }

    private static ushort BaudOf(CanBitRate br) => br switch
    {
        CanBitRate.Br10kBit => PcanApi.PCAN_BAUD_10K,
        CanBitRate.Br20kBit => PcanApi.PCAN_BAUD_20K,
        CanBitRate.Br50kBit => PcanApi.PCAN_BAUD_50K,
        CanBitRate.Br100kBit => PcanApi.PCAN_BAUD_100K,
        CanBitRate.Br125kBit => PcanApi.PCAN_BAUD_125K,
        CanBitRate.Br250kBit => PcanApi.PCAN_BAUD_250K,
        CanBitRate.Br500kBit => PcanApi.PCAN_BAUD_500K,
        CanBitRate.Br800kBit => PcanApi.PCAN_BAUD_800K,
        CanBitRate.Br1000kBit => PcanApi.PCAN_BAUD_1M,
        _ => PcanApi.PCAN_BAUD_500K
    };

    public void Dispose() => Disconnect();
}
