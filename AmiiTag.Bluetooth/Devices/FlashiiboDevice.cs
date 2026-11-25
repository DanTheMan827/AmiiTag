namespace AmiiTag.Bluetooth.Devices;

/// <summary>
/// Flashiibo Pro. It uses the same NUS file-system protocol as the Pixl.js.
/// </summary>
public sealed class FlashiiboDevice : PixlDevice
{
    public FlashiiboDevice(IBleConnection connection) : base(connection)
    {
    }

    public override TagDeviceKind Kind => TagDeviceKind.Flashiibo;

    // Pro Tools requests 517, but deliberately keeps each protocol frame to a 182-byte characteristic value:
    // four header bytes plus 178 payload bytes (a file handle and up to 177 file bytes).
    protected override int RequestedMtu => 517;

    protected override int ProtocolFramePayload => 178;
}
