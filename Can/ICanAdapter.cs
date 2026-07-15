namespace IxxatCanTool.Can;

/// <summary>
/// A CAN adapter backend. Confines all vendor/transport specifics behind one surface so the
/// UI and view model stay adapter-agnostic. Implemented by the Ixxat VCI4 driver
/// (<see cref="CanBusService"/>) and the OBDX Pro tool (<c>ObdxCanAdapter</c>).
/// </summary>
public interface ICanAdapter : IDisposable
{
    /// <summary>Raised for every received (or self-received) frame, on a background thread.</summary>
    event Action<CanFrame>? FrameReceived;

    /// <summary>Raised on a bus error / state change, on a background thread.</summary>
    event Action<string>? BusError;

    bool IsConnected { get; }

    /// <summary>True when cyclic frames transmit without a software timer (hardware scheduler).</summary>
    bool SupportsScheduler { get; }

    /// <summary>Open <paramref name="device"/> at the given bit rate and start receiving.</summary>
    void Connect(CanDeviceInfo device, CanBitRate bitRate, bool listenOnly = false);

    void Disconnect();

    void Send(uint identifier, bool extended, byte[] data, bool remote = false);

    int StartCyclic(uint identifier, bool extended, byte[] data, bool remote, double intervalMs);

    void StopCyclic(int handle);

    void StopAllCyclic();
}

/// <summary>One CAN acceptance-filter entry: an exact ID and its frame width (11- vs 29-bit).</summary>
public readonly record struct CanAcceptId(uint Id, bool Extended);

/// <summary>
/// Optional adapter capability: restrict which CAN IDs are delivered, so a bandwidth-limited link
/// (e.g. OBDX over WiFi) isn't flooded with the whole bus. Call <see cref="SetAcceptanceFilter"/>
/// BEFORE <see cref="ICanAdapter.Connect"/>; an empty set means "receive everything" (the default).
/// Adapters that can't hardware-filter simply don't implement this and keep receiving all frames.
/// </summary>
public interface ICanAcceptanceFilter
{
    /// <summary>Set the exact IDs to accept (applied at the next Connect). Empty/null = accept all.</summary>
    void SetAcceptanceFilter(IReadOnlyCollection<CanAcceptId> ids);
}
