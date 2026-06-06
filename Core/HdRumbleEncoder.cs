namespace Switch2ProWirelessViiper.Core;

public sealed class HdRumbleEncoder
{
    private const ushort DefaultScalePercent = 100;
    private byte _packetId;

    public byte[] BuildStopPacket() => BuildPacket(BuildZeroVibration(), BuildZeroVibration());

    public byte[] BuildSelfTestPacket()
    {
        var left = BuildBleVibrationData(0x0e1, false, 320, 0x1e1, false, 460);
        var right = BuildBleVibrationData(0x0e1, false, 320, 0x1e1, false, 460);
        return BuildPacket(left, right);
    }

    public HdRumbleFrame? TryBuildFromHidOutput(ReadOnlySpan<byte> report)
    {
        if (!IsSwitch2HidRumbleReport(report))
        {
            return null;
        }

        if (!HasNonZeroPayload(report, 2) || IsNeutralSwitch2Rumble(report))
        {
            return new HdRumbleFrame(BuildStopPacket(), Active: false);
        }

        var left = EncodeBleVibrationFromSwitch2Frame(report, 2);
        var right = EncodeBleVibrationFromSwitch2Frame(report, 0x12);
        return new HdRumbleFrame(BuildPacket(left, right), Active: true);
    }

    private byte[] BuildPacket(byte[] left, byte[] right)
    {
        var packet = new byte[33];
        var zero = BuildZeroVibration();
        packet[0] = 0x00;
        WriteMotorBlock(packet, 1, _packetId, left, zero);
        WriteMotorBlock(packet, 17, _packetId, right, zero);
        _packetId = (byte)((_packetId + 1) & 0x0f);
        return packet;
    }

    private static bool IsSwitch2HidRumbleReport(ReadOnlySpan<byte> data) =>
        data.Length >= 7 && data[0] == 0x02 && (data[1] & 0xf0) == 0x50;

    private static bool HasNonZeroPayload(ReadOnlySpan<byte> data, int offset)
    {
        for (var i = offset; i < data.Length; i++)
        {
            if (data[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNeutralSwitch2Rumble(ReadOnlySpan<byte> data) =>
        HasNeutralSwitch2RumbleFrame(data, 2) && HasNeutralSwitch2RumbleFrame(data, 0x12);

    private static bool HasNeutralSwitch2RumbleFrame(ReadOnlySpan<byte> data, int offset) =>
        data.Length >= offset + 5 &&
        data[offset] == 0x87 &&
        data[offset + 1] == 0x01 &&
        data[offset + 2] == 0x20 &&
        data[offset + 3] == 0x11 &&
        data[offset + 4] == 0x00;

    private static byte[] EncodeBleVibrationFromSwitch2Frame(ReadOnlySpan<byte> report, int offset)
    {
        if (report.Length < offset + 5)
        {
            return BuildZeroVibration();
        }

        var b0 = report[offset];
        var b1 = report[offset + 1];
        var b2 = report[offset + 2];
        var b3 = report[offset + 3];
        var b4 = report[offset + 4];

        var highFreq = b0 | ((b1 & 0x03) << 8);
        var highAmp = ((b1 & 0xfc) << 4) | ((b2 & 0x0f) << 12);
        var lowFreq = ((b2 & 0xf0) >> 4) | ((b3 & 0x3f) << 4);
        var lowAmp = (b3 & 0xc0) | (b4 << 8);

        return BuildBleVibrationData(
            (ushort)lowFreq,
            false,
            (ushort)MapSwitchAmpToBle(lowAmp),
            (ushort)highFreq,
            false,
            (ushort)MapSwitchAmpToBle(highAmp));
    }

    private static int MapSwitchAmpToBle(int value)
    {
        var scaled = (long)value * 1023L * DefaultScalePercent;
        return (int)Math.Clamp((scaled + 1_450_000L) / 2_900_000L, 0, 1023);
    }

    private static byte[] BuildZeroVibration() =>
        BuildBleVibrationData(0x0e1, false, 0, 0x1e1, false, 0);

    private static byte[] BuildBleVibrationData(
        ushort lowFreq,
        bool lowTone,
        ushort lowAmp,
        ushort highFreq,
        bool highTone,
        ushort highAmp)
    {
        ulong value = 0;
        value |= ((ulong)lowFreq) & 0x01ffUL;
        value |= (ulong)(lowTone ? 1u : 0u) << 9;
        value |= (((ulong)lowAmp) & 0x03ffUL) << 10;
        value |= (((ulong)highFreq) & 0x01ffUL) << 20;
        value |= (ulong)(highTone ? 1u : 0u) << 29;
        value |= (((ulong)highAmp) & 0x03ffUL) << 30;

        var outData = new byte[5];
        for (var i = 0; i < outData.Length; i++)
        {
            outData[i] = (byte)((value >> (8 * i)) & 0xff);
        }

        return outData;
    }

    private static void WriteMotorBlock(Span<byte> output, int offset, byte packetId, byte[] first, byte[] zero)
    {
        output[offset] = (byte)(0x50 | (packetId & 0x0f));
        first.AsSpan(0, 5).CopyTo(output[(offset + 1)..]);
        zero.AsSpan(0, 5).CopyTo(output[(offset + 6)..]);
        zero.AsSpan(0, 5).CopyTo(output[(offset + 11)..]);
    }
}

public sealed record HdRumbleFrame(byte[] Packet, bool Active);
