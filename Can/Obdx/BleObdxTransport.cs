namespace IxxatCanTool.Can.Obdx;

// net48 stub. The real BLE transport in UltimateCANScanner uses WinRT Bluetooth APIs that
// require the net8 Windows SDK TFM. The feeder targets .NET Framework 4.8 for DynoDotNet.dll
// interop, so BLE is not available here — use OBDX over USB (serial:) or WiFi (tcp:) instead.
// If BLE is ever required, retarget the feeder or split BLE into a net8 helper process.
internal sealed class BleObdxTransport : IObdxTransport
{
    public BleObdxTransport(ulong address) => throw NotSupported();
    public BleObdxTransport(int _) => throw NotSupported();

    private static NotSupportedException NotSupported() =>
        new("OBDX BLE is not supported in the net48 feeder build. Use OBDX USB (serial:) or WiFi (tcp:).");

    // IObdxTransport members (never reached — the constructor throws first).
    public void Open() => throw NotSupported();
    public void Write(byte[] data) => throw NotSupported();
    public int Read(byte[] buffer) => throw NotSupported();
    public void Close() { }
    public void Dispose() { }
}
