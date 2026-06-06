namespace Switch2ProWirelessViiper.Core;

[Flags]
public enum Switch2Button : uint
{
    None = 0,
    B = 1u << 0,
    A = 1u << 1,
    Y = 1u << 2,
    X = 1u << 3,
    R = 1u << 4,
    ZR = 1u << 5,
    Plus = 1u << 6,
    RStick = 1u << 7,
    DDown = 1u << 8,
    DRight = 1u << 9,
    DLeft = 1u << 10,
    DUp = 1u << 11,
    L = 1u << 12,
    ZL = 1u << 13,
    Minus = 1u << 14,
    LStick = 1u << 15,
    Home = 1u << 16,
    Capture = 1u << 17,
    GR = 1u << 18,
    GL = 1u << 19,
    C = 1u << 20,
}

public sealed class Switch2State
{
    public const ushort StickCenter = 2048;
    public const int MotionSampleSize = 12;

    public Switch2Button Buttons { get; set; }
    public ushort LeftX { get; set; } = StickCenter;
    public ushort LeftY { get; set; } = StickCenter;
    public ushort RightX { get; set; } = StickCenter;
    public ushort RightY { get; set; } = StickCenter;
    public bool MotionValid { get; private set; }
    public byte[] Motion { get; } = new byte[MotionSampleSize];

    public void ResetControls()
    {
        Buttons = Switch2Button.None;
        LeftX = StickCenter;
        LeftY = StickCenter;
        RightX = StickCenter;
        RightY = StickCenter;
    }

    public void SetMotion(ReadOnlySpan<byte> sample)
    {
        if (sample.Length < MotionSampleSize)
        {
            return;
        }

        sample[..MotionSampleSize].CopyTo(Motion);
        MotionValid = true;
    }

    public void CopyFrom(Switch2State source)
    {
        Buttons = source.Buttons;
        LeftX = source.LeftX;
        LeftY = source.LeftY;
        RightX = source.RightX;
        RightY = source.RightY;
        MotionValid = source.MotionValid;
        source.Motion.CopyTo(Motion, 0);
    }
}
