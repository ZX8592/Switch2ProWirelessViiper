namespace Switch2ProWirelessViiper.Core;

public sealed class Fd2ReportParser
{
    public const string Fd2NotifyUuid = "ab7de9be-89fe-49ad-828f-118f09df7fd2";
    public const string LegacyNotifyUuid = "ab7de9be-89fe-49ad-828f-118f09df7fc0";

    private const int AxisCalibrationSamples = 20;
    private const double AxisOutputScale = 1.6;
    private const int MinimumCalibratedAxisRange = 256;
    private const int Fd2FullReportMinLen = 60;
    private const int Fd2MotionOffset = 48;

    private readonly AxisCalibration _fd2Axis = new();
    private readonly AxisCalibration _legacyAxis = new();

    public bool HasStickCenterCalibration => _fd2Axis.Calibrated || _legacyAxis.Calibrated;

    public void ResetStickCalibration()
    {
        _fd2Axis.Reset();
        _legacyAxis.Reset();
    }

    public void ApplyStickCalibration(StickCalibrationProfile? profile)
    {
        if (profile is not null && profile.IsUsable)
        {
            _fd2Axis.Apply(profile);
        }
    }

    public void BeginStickRangeCalibration()
    {
        _fd2Axis.BeginRangeCalibration();
        _legacyAxis.BeginRangeCalibration();
    }

    public StickCalibrationProfile? EndStickRangeCalibration()
    {
        var fd2 = _fd2Axis.EndRangeCalibration();
        _legacyAxis.EndRangeCalibration();
        return fd2;
    }

    public bool TryParse(string characteristicUuid, ReadOnlySpan<byte> data, Switch2State state)
    {
        if (characteristicUuid.Equals(Fd2NotifyUuid, StringComparison.OrdinalIgnoreCase) && data.Length >= 8)
        {
            ApplyFd2Buttons(state, ReadLe32(data[4..]));
            if (data.Length >= 16)
            {
                ApplyAxes(
                    _fd2Axis,
                    state,
                    Unpack12X(data, 10),
                    Unpack12Y(data, 10),
                    Unpack12X(data, 13),
                    Unpack12Y(data, 13));
            }

            if (data.Length >= Fd2FullReportMinLen && data.Length >= Fd2MotionOffset + Switch2State.MotionSampleSize)
            {
                state.SetMotion(data.Slice(Fd2MotionOffset, Switch2State.MotionSampleSize));
            }

            return true;
        }

        if (characteristicUuid.Equals(LegacyNotifyUuid, StringComparison.OrdinalIgnoreCase) && data.Length >= 5)
        {
            ApplyLegacyButtons(state, data[2], data[3], data[4]);
            if (data.Length >= 11)
            {
                ApplyAxes(
                    _legacyAxis,
                    state,
                    Unpack12X(data, 5),
                    Unpack12Y(data, 5),
                    Unpack12X(data, 8),
                    Unpack12Y(data, 8));
            }

            return true;
        }

        return false;
    }

    private static uint ReadLe32(ReadOnlySpan<byte> data) =>
        (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));

    private static ushort Clamp12(int value) => (ushort)Math.Clamp(value, 0, 4095);

    private static ushort Unpack12X(ReadOnlySpan<byte> data, int offset) =>
        Clamp12(data[offset] | ((data[offset + 1] & 0x0f) << 8));

    private static ushort Unpack12Y(ReadOnlySpan<byte> data, int offset) =>
        Clamp12(((data[offset + 1] >> 4) & 0x0f) | (data[offset + 2] << 4));

    private static ushort MapAxis(
        ushort value,
        ushort center,
        ushort min,
        ushort max,
        bool endpointCalibrated)
    {
        var delta = value - center;

        if (endpointCalibrated)
        {
            if (delta < 0 && center - min >= MinimumCalibratedAxisRange)
            {
                var magnitude = (int)Math.Round((-delta) * (Switch2State.StickCenter / (double)(center - min)));
                return Clamp12(Switch2State.StickCenter - magnitude);
            }

            if (delta > 0 && max - center >= MinimumCalibratedAxisRange)
            {
                var magnitude = (int)Math.Round(delta * ((4095 - Switch2State.StickCenter) / (double)(max - center)));
                return Clamp12(Switch2State.StickCenter + magnitude);
            }
        }

        return Clamp12(Switch2State.StickCenter + (int)Math.Round(delta * AxisOutputScale));
    }

    private static void CaptureRangeIfNeeded(
        AxisCalibration calibration,
        ushort lx,
        ushort ly,
        ushort rx,
        ushort ry)
    {
        if (!calibration.RangeCalibrating)
        {
            return;
        }

        calibration.RangeSampleCount++;
        calibration.MinLx = Math.Min(calibration.MinLx, lx);
        calibration.MaxLx = Math.Max(calibration.MaxLx, lx);
        calibration.MinLy = Math.Min(calibration.MinLy, ly);
        calibration.MaxLy = Math.Max(calibration.MaxLy, ly);
        calibration.MinRx = Math.Min(calibration.MinRx, rx);
        calibration.MaxRx = Math.Max(calibration.MaxRx, rx);
        calibration.MinRy = Math.Min(calibration.MinRy, ry);
        calibration.MaxRy = Math.Max(calibration.MaxRy, ry);
    }

    private static void ApplyAxes(
        AxisCalibration calibration,
        Switch2State state,
        ushort lx,
        ushort ly,
        ushort rx,
        ushort ry)
    {
        if (!calibration.Calibrated && state.Buttons == Switch2Button.None)
        {
            calibration.SumLx += lx;
            calibration.SumLy += ly;
            calibration.SumRx += rx;
            calibration.SumRy += ry;
            calibration.SampleCount++;

            if (calibration.SampleCount >= AxisCalibrationSamples)
            {
                calibration.CenterLx = (ushort)(calibration.SumLx / calibration.SampleCount);
                calibration.CenterLy = (ushort)(calibration.SumLy / calibration.SampleCount);
                calibration.CenterRx = (ushort)(calibration.SumRx / calibration.SampleCount);
                calibration.CenterRy = (ushort)(calibration.SumRy / calibration.SampleCount);
                calibration.Calibrated = true;
            }
        }

        if (!calibration.Calibrated)
        {
            state.LeftX = Switch2State.StickCenter;
            state.LeftY = Switch2State.StickCenter;
            state.RightX = Switch2State.StickCenter;
            state.RightY = Switch2State.StickCenter;
            return;
        }

        CaptureRangeIfNeeded(calibration, lx, ly, rx, ry);

        state.LeftX = MapAxis(lx, calibration.CenterLx, calibration.MinLx, calibration.MaxLx, calibration.EndpointCalibrated);
        state.LeftY = MapAxis(ly, calibration.CenterLy, calibration.MinLy, calibration.MaxLy, calibration.EndpointCalibrated);
        state.RightX = MapAxis(rx, calibration.CenterRx, calibration.MinRx, calibration.MaxRx, calibration.EndpointCalibrated);
        state.RightY = MapAxis(ry, calibration.CenterRy, calibration.MinRy, calibration.MaxRy, calibration.EndpointCalibrated);
    }

    private static void ApplyLegacyButtons(Switch2State state, byte b2, byte b3, byte b4)
    {
        state.ResetControls();
        Set(state, Switch2Button.B, (b2 & 0x01) != 0);
        Set(state, Switch2Button.A, (b2 & 0x02) != 0);
        Set(state, Switch2Button.Y, (b2 & 0x04) != 0);
        Set(state, Switch2Button.X, (b2 & 0x08) != 0);
        Set(state, Switch2Button.R, (b2 & 0x10) != 0);
        Set(state, Switch2Button.ZR, (b2 & 0x20) != 0);
        Set(state, Switch2Button.Plus, (b2 & 0x40) != 0);
        Set(state, Switch2Button.RStick, (b2 & 0x80) != 0);
        Set(state, Switch2Button.DDown, (b3 & 0x01) != 0);
        Set(state, Switch2Button.DRight, (b3 & 0x02) != 0);
        Set(state, Switch2Button.DLeft, (b3 & 0x04) != 0);
        Set(state, Switch2Button.DUp, (b3 & 0x08) != 0);
        Set(state, Switch2Button.L, (b3 & 0x10) != 0);
        Set(state, Switch2Button.ZL, (b3 & 0x20) != 0);
        Set(state, Switch2Button.Minus, (b3 & 0x40) != 0);
        Set(state, Switch2Button.LStick, (b3 & 0x80) != 0);
        Set(state, Switch2Button.Home, (b4 & 0x01) != 0);
        Set(state, Switch2Button.Capture, (b4 & 0x02) != 0);
        Set(state, Switch2Button.GR, (b4 & 0x04) != 0);
        Set(state, Switch2Button.GL, (b4 & 0x08) != 0);
        Set(state, Switch2Button.C, (b4 & 0x10) != 0);
    }

    private static void ApplyFd2Buttons(Switch2State state, uint buttons)
    {
        state.ResetControls();
        Set(state, Switch2Button.Y, (buttons & 0x00000001) != 0);
        Set(state, Switch2Button.X, (buttons & 0x00000002) != 0);
        Set(state, Switch2Button.B, (buttons & 0x00000004) != 0);
        Set(state, Switch2Button.A, (buttons & 0x00000008) != 0);
        Set(state, Switch2Button.R, (buttons & 0x00000040) != 0);
        Set(state, Switch2Button.ZR, (buttons & 0x00000080) != 0);
        Set(state, Switch2Button.Minus, (buttons & 0x00000100) != 0);
        Set(state, Switch2Button.Plus, (buttons & 0x00000200) != 0);
        Set(state, Switch2Button.RStick, (buttons & 0x00000400) != 0);
        Set(state, Switch2Button.LStick, (buttons & 0x00000800) != 0);
        Set(state, Switch2Button.Home, (buttons & 0x00001000) != 0);
        Set(state, Switch2Button.Capture, (buttons & 0x00002000) != 0);
        Set(state, Switch2Button.C, (buttons & 0x00004000) != 0);
        Set(state, Switch2Button.DDown, (buttons & 0x00010000) != 0);
        Set(state, Switch2Button.DUp, (buttons & 0x00020000) != 0);
        Set(state, Switch2Button.DRight, (buttons & 0x00040000) != 0);
        Set(state, Switch2Button.DLeft, (buttons & 0x00080000) != 0);
        Set(state, Switch2Button.L, (buttons & 0x00400000) != 0);
        Set(state, Switch2Button.ZL, (buttons & 0x00800000) != 0);
        Set(state, Switch2Button.GR, (buttons & 0x01000000) != 0);
        Set(state, Switch2Button.GL, (buttons & 0x02000000) != 0);
    }

    private static void Set(Switch2State state, Switch2Button button, bool pressed)
    {
        if (pressed)
        {
            state.Buttons |= button;
        }
        else
        {
            state.Buttons &= ~button;
        }
    }

    private sealed class AxisCalibration
    {
        public bool Calibrated { get; set; }
        public uint SampleCount { get; set; }
        public uint SumLx { get; set; }
        public uint SumLy { get; set; }
        public uint SumRx { get; set; }
        public uint SumRy { get; set; }
        public ushort CenterLx { get; set; } = Switch2State.StickCenter;
        public ushort CenterLy { get; set; } = Switch2State.StickCenter;
        public ushort CenterRx { get; set; } = Switch2State.StickCenter;
        public ushort CenterRy { get; set; } = Switch2State.StickCenter;
        public bool RangeCalibrating { get; set; }
        public bool EndpointCalibrated { get; set; }
        public uint RangeSampleCount { get; set; }
        public ushort MinLx { get; set; } = Switch2State.StickCenter;
        public ushort MaxLx { get; set; } = Switch2State.StickCenter;
        public ushort MinLy { get; set; } = Switch2State.StickCenter;
        public ushort MaxLy { get; set; } = Switch2State.StickCenter;
        public ushort MinRx { get; set; } = Switch2State.StickCenter;
        public ushort MaxRx { get; set; } = Switch2State.StickCenter;
        public ushort MinRy { get; set; } = Switch2State.StickCenter;
        public ushort MaxRy { get; set; } = Switch2State.StickCenter;

        public void Reset()
        {
            Calibrated = false;
            SampleCount = 0;
            SumLx = 0;
            SumLy = 0;
            SumRx = 0;
            SumRy = 0;
            CenterLx = Switch2State.StickCenter;
            CenterLy = Switch2State.StickCenter;
            CenterRx = Switch2State.StickCenter;
            CenterRy = Switch2State.StickCenter;
            BeginRangeCalibration();
            RangeCalibrating = false;
            EndpointCalibrated = false;
        }

        public void BeginRangeCalibration()
        {
            RangeCalibrating = true;
            EndpointCalibrated = false;
            RangeSampleCount = 0;
            MinLx = MaxLx = CenterLx;
            MinLy = MaxLy = CenterLy;
            MinRx = MaxRx = CenterRx;
            MinRy = MaxRy = CenterRy;
        }

        public StickCalibrationProfile? EndRangeCalibration()
        {
            RangeCalibrating = false;
            if (!Calibrated || RangeSampleCount < 10 || !HasUsableEndpoints())
            {
                return null;
            }

            EndpointCalibrated = true;
            return ToProfile();
        }

        public void Apply(StickCalibrationProfile profile)
        {
            Calibrated = true;
            EndpointCalibrated = true;
            CenterLx = profile.CenterLx;
            CenterLy = profile.CenterLy;
            CenterRx = profile.CenterRx;
            CenterRy = profile.CenterRy;
            MinLx = profile.MinLx;
            MaxLx = profile.MaxLx;
            MinLy = profile.MinLy;
            MaxLy = profile.MaxLy;
            MinRx = profile.MinRx;
            MaxRx = profile.MaxRx;
            MinRy = profile.MinRy;
            MaxRy = profile.MaxRy;
        }

        private bool HasUsableEndpoints() =>
            CenterLx - MinLx >= MinimumCalibratedAxisRange &&
            MaxLx - CenterLx >= MinimumCalibratedAxisRange &&
            CenterLy - MinLy >= MinimumCalibratedAxisRange &&
            MaxLy - CenterLy >= MinimumCalibratedAxisRange &&
            CenterRx - MinRx >= MinimumCalibratedAxisRange &&
            MaxRx - CenterRx >= MinimumCalibratedAxisRange &&
            CenterRy - MinRy >= MinimumCalibratedAxisRange &&
            MaxRy - CenterRy >= MinimumCalibratedAxisRange;

        private StickCalibrationProfile ToProfile() => new()
        {
            CenterLx = CenterLx,
            CenterLy = CenterLy,
            CenterRx = CenterRx,
            CenterRy = CenterRy,
            MinLx = MinLx,
            MaxLx = MaxLx,
            MinLy = MinLy,
            MaxLy = MaxLy,
            MinRx = MinRx,
            MaxRx = MaxRx,
            MinRy = MinRy,
            MaxRy = MaxRy,
        };
    }
}

public sealed class StickCalibrationProfile
{
    public ushort CenterLx { get; set; } = Switch2State.StickCenter;
    public ushort CenterLy { get; set; } = Switch2State.StickCenter;
    public ushort CenterRx { get; set; } = Switch2State.StickCenter;
    public ushort CenterRy { get; set; } = Switch2State.StickCenter;
    public ushort MinLx { get; set; } = Switch2State.StickCenter;
    public ushort MaxLx { get; set; } = Switch2State.StickCenter;
    public ushort MinLy { get; set; } = Switch2State.StickCenter;
    public ushort MaxLy { get; set; } = Switch2State.StickCenter;
    public ushort MinRx { get; set; } = Switch2State.StickCenter;
    public ushort MaxRx { get; set; } = Switch2State.StickCenter;
    public ushort MinRy { get; set; } = Switch2State.StickCenter;
    public ushort MaxRy { get; set; } = Switch2State.StickCenter;

    public bool IsUsable =>
        CenterLx > MinLx && MaxLx > CenterLx &&
        CenterLy > MinLy && MaxLy > CenterLy &&
        CenterRx > MinRx && MaxRx > CenterRx &&
        CenterRy > MinRy && MaxRy > CenterRy;
}
