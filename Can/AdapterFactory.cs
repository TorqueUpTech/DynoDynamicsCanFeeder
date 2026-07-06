namespace IxxatCanTool.Can;

// Feeder-local adapter factory. Unlike the UltimateCANScanner original it deliberately
// omits J2534 (not needed here) so the feeder only pulls in the IXXAT + OBDX backends.
public static class AdapterFactory
{
    public static IReadOnlyList<CanDeviceInfo> Enumerate(CanAdapterKind kind) => kind switch
    {
        CanAdapterKind.IxxatVci => CanBusService.EnumerateDevices(),
        CanAdapterKind.Obdx     => Obdx.ObdxCanAdapter.EnumerateDevices(),
        _ => new List<CanDeviceInfo>()
    };

    public static ICanAdapter Create(CanAdapterKind kind) => kind switch
    {
        CanAdapterKind.IxxatVci => new CanBusService(),
        CanAdapterKind.Obdx     => new Obdx.ObdxCanAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported adapter for the feeder.")
    };
}
