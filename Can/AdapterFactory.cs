namespace IxxatCanTool.Can;

// Feeder-local adapter factory. Backends: IXXAT VCI4, OBDX, generic SAE J2534 PassThru
// (any installed vendor DLL), SavvyCAN/GVRET serial/TCP loggers, and PEAK PCAN-USB.
public static class AdapterFactory
{
    public static IReadOnlyList<CanDeviceInfo> Enumerate(CanAdapterKind kind) => kind switch
    {
        CanAdapterKind.IxxatVci => CanBusService.EnumerateDevices(),
        CanAdapterKind.Obdx     => Obdx.ObdxCanAdapter.EnumerateDevices(),
        CanAdapterKind.J2534    => J2534.J2534CanAdapter.EnumerateDevices(),
        CanAdapterKind.Gvret    => Gvret.GvretCanAdapter.EnumerateDevices(),
        CanAdapterKind.Peak     => Peak.PcanCanAdapter.EnumerateDevices(),
        CanAdapterKind.VirtualTcp => VirtualTcp.VirtualTcpCanAdapter.EnumerateDevices(),
        _ => new List<CanDeviceInfo>()
    };

    public static ICanAdapter Create(CanAdapterKind kind) => kind switch
    {
        CanAdapterKind.IxxatVci => new CanBusService(),
        CanAdapterKind.Obdx     => new Obdx.ObdxCanAdapter(),
        CanAdapterKind.J2534    => new J2534.J2534CanAdapter(),
        CanAdapterKind.Gvret    => new Gvret.GvretCanAdapter(),
        CanAdapterKind.Peak     => new Peak.PcanCanAdapter(),
        CanAdapterKind.VirtualTcp => new VirtualTcp.VirtualTcpCanAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported adapter for the feeder.")
    };
}
