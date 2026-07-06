using System.Runtime.InteropServices;
using System.Text;

namespace IxxatCanTool.Can.Peak;

/// <summary>
/// Minimal P/Invoke surface for PEAK <c>PCANBasic.dll</c> (the classic, non-FD API). The 64-bit
/// driver installs as <c>%WINDIR%\System32\PCANBasic.dll</c>, so a plain <c>DllImport</c> resolves
/// it on this x64 process. Only the handful of entry points the feeder needs are bound.
/// </summary>
internal static class PcanApi
{
    // ---- Channel handles (TPCANHandle, ushort) ----
    public const ushort PCAN_NONEBUS = 0x00;
    public const ushort PCAN_USBBUS1 = 0x51; // USB channels 1..8 are 0x51..0x58
    public const ushort PCAN_USBBUS9 = 0x509; // channels 9..16 are 0x509..0x510

    // ---- Baud rates (TPCANBaudrate, ushort BTR0BTR1) ----
    public const ushort PCAN_BAUD_1M = 0x0014;
    public const ushort PCAN_BAUD_800K = 0x0016;
    public const ushort PCAN_BAUD_500K = 0x001C;
    public const ushort PCAN_BAUD_250K = 0x011C;
    public const ushort PCAN_BAUD_125K = 0x031C;
    public const ushort PCAN_BAUD_100K = 0x432F;
    public const ushort PCAN_BAUD_50K = 0x472F;
    public const ushort PCAN_BAUD_20K = 0x532F;
    public const ushort PCAN_BAUD_10K = 0x672F;

    // ---- Message type flags (TPCANMessageType, byte) ----
    public const byte PCAN_MESSAGE_STANDARD = 0x00;
    public const byte PCAN_MESSAGE_RTR = 0x01;
    public const byte PCAN_MESSAGE_EXTENDED = 0x02;
    public const byte PCAN_MESSAGE_STATUS = 0x80;

    // ---- Parameters (TPCANParameter, byte) ----
    public const byte PCAN_CHANNEL_CONDITION = 0x2B;
    public const byte PCAN_LISTEN_ONLY = 0x08;

    public const uint PCAN_PARAMETER_OFF = 0x00;
    public const uint PCAN_PARAMETER_ON = 0x01;

    // Channel-condition bits returned by GetValue(PCAN_CHANNEL_CONDITION).
    public const uint PCAN_CHANNEL_UNAVAILABLE = 0x00;
    public const uint PCAN_CHANNEL_AVAILABLE = 0x01;
    public const uint PCAN_CHANNEL_OCCUPIED = 0x02;

    // ---- Status codes (TPCANStatus, uint) ----
    public const uint PCAN_ERROR_OK = 0x00000;
    public const uint PCAN_ERROR_QRCVEMPTY = 0x00020; // receive queue empty (normal when idle)
    public const uint PCAN_ERROR_QOVERRUN = 0x00040;
    public const uint PCAN_ERROR_BUSLIGHT = 0x00200;
    public const uint PCAN_ERROR_BUSHEAVY = 0x00400;
    public const uint PCAN_ERROR_BUSOFF = 0x00800;

    private const string Dll = "PCANBasic.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    public static extern uint CAN_Initialize(ushort channel, ushort btr0btr1, byte hwType, uint ioPort, ushort interrupt);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    public static extern uint CAN_Uninitialize(ushort channel);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    public static extern uint CAN_Read(ushort channel, out TPCANMsg message, out TPCANTimestamp timestamp);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    public static extern uint CAN_Write(ushort channel, ref TPCANMsg message);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    public static extern uint CAN_SetValue(ushort channel, byte parameter, ref uint buffer, uint bufferLength);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    public static extern uint CAN_GetValue(ushort channel, byte parameter, out uint buffer, uint bufferLength);

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    public static extern uint CAN_GetErrorText(uint error, ushort language, StringBuilder buffer);

    /// <summary>Human-readable text for a TPCANStatus (English), best-effort.</summary>
    public static string ErrorText(uint status)
    {
        try
        {
            var sb = new StringBuilder(256);
            if (CAN_GetErrorText(status, 0, sb) == PCAN_ERROR_OK)
                return sb.ToString();
        }
        catch { /* driver missing */ }
        return $"0x{status:X}";
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct TPCANMsg
{
    public uint ID;
    public byte MSGTYPE;
    public byte LEN;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public byte[] DATA;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TPCANTimestamp
{
    public uint millis;
    public ushort millis_overflow;
    public ushort micros;
}
