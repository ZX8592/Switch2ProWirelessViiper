using System.Buffers.Binary;

namespace Switch2ProWirelessViiper.Core;

public static class ViiperNs2ProWire
{
    public const int InputSize = 24;
    public const int OutputSize = 34;
    public const byte OutputFlagRumble = 0x01;

    public static byte[] BuildInput(Switch2State state)
    {
        var wire = new byte[InputSize];
        WriteInput(wire, state);
        return wire;
    }

    public static void WriteInput(Span<byte> wire, Switch2State state)
    {
        if (wire.Length < InputSize)
        {
            throw new ArgumentException($"Expected at least {InputSize} bytes for VIIPER ns2pro input.", nameof(wire));
        }

        wire[..InputSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(wire[0..4], (uint)state.Buttons);
        BinaryPrimitives.WriteUInt16LittleEndian(wire[4..6], ClampStick(state.LeftX));
        BinaryPrimitives.WriteUInt16LittleEndian(wire[6..8], ClampStick(state.LeftY));
        BinaryPrimitives.WriteUInt16LittleEndian(wire[8..10], ClampStick(state.RightX));
        BinaryPrimitives.WriteUInt16LittleEndian(wire[10..12], ClampStick(state.RightY));

        if (state.MotionValid)
        {
            state.Motion.AsSpan(0, Switch2State.MotionSampleSize)
                .CopyTo(wire[12..(12 + Switch2State.MotionSampleSize)]);
        }
    }

    public static byte[] BuildHidOutputFromFeedback(ReadOnlySpan<byte> viiperOutput)
    {
        if (viiperOutput.Length < OutputSize)
        {
            throw new ArgumentException($"Expected {OutputSize} bytes of VIIPER ns2pro feedback.", nameof(viiperOutput));
        }

        var report = new byte[33];
        report[0] = 0x02;
        viiperOutput.Slice(0, 16).CopyTo(report.AsSpan(1, 16));
        viiperOutput.Slice(16, 16).CopyTo(report.AsSpan(17, 16));
        return report;
    }

    private static ushort ClampStick(ushort value) => value > 0x0fff ? (ushort)0x0fff : value;
}
