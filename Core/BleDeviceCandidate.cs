namespace Switch2ProWirelessViiper.Core;

public sealed record BleDeviceCandidate(
    ulong BluetoothAddress,
    string? Name,
    short Rssi,
    DateTimeOffset LastSeen)
{
    public override string ToString() =>
        $"{BluetoothAddress:X12}  RSSI {Rssi}  {Name ?? "(unknown)"}";
}
