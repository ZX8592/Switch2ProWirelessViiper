using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Switch2ProWirelessViiper.Core;

public sealed class BleClient : IAsyncDisposable
{
    public const string InputReportUuid = "ab7de9be-89fe-49ad-828f-118f09df7fd2";
    public const string AckReportUuid = "c765a961-d9d8-4d36-a20a-5315b111836a";
    public const string WriteCommandUuid = "649d4ac9-8eb7-4e6c-af44-1ea54fe5f005";
    public const string RumbleCommandUuid = "cc483f51-9258-427d-a939-630c31f72b05";

    private BluetoothLEDevice? _device;
    private GattSession? _gattSession;
    private BluetoothLEPreferredConnectionParametersRequest? _connectionParametersRequest;
    private GattCharacteristic? _inputCharacteristic;
    private GattCharacteristic? _ackCharacteristic;
    private GattCharacteristic? _writeCharacteristic;
    private GattCharacteristic? _rumbleCharacteristic;
    private readonly List<GattDeviceService> _services = new();
    private readonly object _ackLock = new();
    private readonly object _notificationDiagnosticsLock = new();
    private TaskCompletionSource<byte[]>? _pendingAck;
    private long _notificationWindowStartedTicks = Stopwatch.GetTimestamp();
    private long _inputNotifications;
    private long _ackNotifications;
    private long _otherNotifications;
    private long _inputBytes;
    private long _inputIntervalTicks;
    private long _inputIntervalSamples;
    private long _lastInputNotificationTicks;
    private long _minimumInputIntervalTicks = long.MaxValue;
    private long _maximumInputIntervalTicks;
    private int _minimumInputSize = int.MaxValue;
    private int _maximumInputSize;

    public event EventHandler<BleNotificationEventArgs>? NotificationReceived;
    public event EventHandler<BluetoothConnectionStatus>? ConnectionStatusChanged;
    public event EventHandler<string>? Trace;
    public event EventHandler<string>? LinkDiagnosticsChanged;

    public bool IsConnected => _device is not null && _inputCharacteristic is not null && _writeCharacteristic is not null;

    public async Task ConnectAsync(ulong bluetoothAddress, CancellationToken cancellationToken)
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (_device is null)
        {
            throw new InvalidOperationException($"Could not connect to BLE device {bluetoothAddress:X12}.");
        }

        Trace?.Invoke(
            this,
            $"BLE device opened: name='{_device.Name}', address={_device.BluetoothAddress:X12}, " +
            $"status={_device.ConnectionStatus}, paired={_device.DeviceInformation.Pairing.IsPaired}, " +
            $"id='{_device.DeviceInformation.Id}'.");
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        ReassertLowLatencyConnectionParameters("device-open");
        await OpenGattSessionAsync(cancellationToken).ConfigureAwait(false);
        await DiscoverCharacteristicsAsync(cancellationToken).ConfigureAwait(false);
        await SubscribeCharacteristicAsync(_ackCharacteristic, "ACK", cancellationToken).ConfigureAwait(false);
        await SendInitializationAsync(cancellationToken).ConfigureAwait(false);
        await SubscribeCharacteristicAsync(_inputCharacteristic, "FD2 input", cancellationToken).ConfigureAwait(false);
        ReassertLowLatencyConnectionParameters("fd2-subscribed");
    }

    public void ReassertLowLatencyConnectionParameters(string reason)
    {
        RequestLowLatencyConnectionParameters(reason);
    }

    public async Task WriteRumbleAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var target = _rumbleCharacteristic ?? _writeCharacteristic;
        if (target is null)
        {
            throw new InvalidOperationException("BLE rumble/write characteristic is not available.");
        }

        await WriteCharacteristicAsync(target, data, "rumble", cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectControllerAsync(CancellationToken cancellationToken)
    {
        try
        {
            byte[] ledOff = [0x09, 0x91, 0x01, 0x07, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
            await WriteCommandAsync(ledOff, cancellationToken).ConfigureAwait(false);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }

        if (_gattSession is not null)
        {
            _gattSession.MaintainConnection = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var characteristic in new[] { _inputCharacteristic, _ackCharacteristic })
        {
            if (characteristic is null)
            {
                continue;
            }

            characteristic.ValueChanged -= OnValueChanged;
            try
            {
                await characteristic
                    .WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None)
                    .AsTask()
                    .ConfigureAwait(false);
            }
            catch
            {
            }
        }

        foreach (var service in _services)
        {
            try { service.Dispose(); } catch { }
        }
        _services.Clear();

        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        }

        _connectionParametersRequest?.Dispose();
        _connectionParametersRequest = null;
        if (_gattSession is not null)
        {
            _gattSession.SessionStatusChanged -= OnGattSessionStatusChanged;
            _gattSession.MaxPduSizeChanged -= OnGattSessionMaxPduSizeChanged;
        }

        _gattSession?.Dispose();
        _gattSession = null;
        _device?.Dispose();
        _device = null;
        _inputCharacteristic = null;
        _ackCharacteristic = null;
        _writeCharacteristic = null;
        _rumbleCharacteristic = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private async Task DiscoverCharacteristicsAsync(CancellationToken cancellationToken)
    {
        if (_device is null)
        {
            throw new InvalidOperationException("BLE device is not connected.");
        }

        var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (servicesResult.Status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException(
                $"GATT service discovery failed: {servicesResult.Status}, protocolError={servicesResult.ProtocolError?.ToString() ?? "none"}");
        }

        Trace?.Invoke(this, $"GATT service discovery returned {servicesResult.Services.Count} service(s).");
        foreach (var service in servicesResult.Services)
        {
            _services.Add(service);
            var characteristicsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            if (characteristicsResult.Status != GattCommunicationStatus.Success)
            {
                Trace?.Invoke(
                    this,
                    $"GATT characteristic discovery skipped service {service.Uuid}: " +
                    $"status={characteristicsResult.Status}, protocolError={characteristicsResult.ProtocolError?.ToString() ?? "none"}.");
                continue;
            }

            foreach (var characteristic in characteristicsResult.Characteristics)
            {
                var uuid = characteristic.Uuid.ToString();
                if (uuid.Equals(InputReportUuid, StringComparison.OrdinalIgnoreCase))
                {
                    _inputCharacteristic = characteristic;
                }
                else if (uuid.Equals(AckReportUuid, StringComparison.OrdinalIgnoreCase))
                {
                    _ackCharacteristic = characteristic;
                }
                else if (uuid.Equals(WriteCommandUuid, StringComparison.OrdinalIgnoreCase))
                {
                    _writeCharacteristic = characteristic;
                }
                else if (uuid.Equals(RumbleCommandUuid, StringComparison.OrdinalIgnoreCase))
                {
                    _rumbleCharacteristic = characteristic;
                }
            }
        }

        if (_inputCharacteristic is null || _writeCharacteristic is null)
        {
            throw new InvalidOperationException(
                $"Required GATT characteristics were not found. Input={_inputCharacteristic is not null}, Write={_writeCharacteristic is not null}");
        }

        Trace?.Invoke(
            this,
            $"GATT ready input={DescribeCharacteristic(_inputCharacteristic)}, " +
            $"ack={DescribeCharacteristic(_ackCharacteristic)}, " +
            $"cmd={DescribeCharacteristic(_writeCharacteristic)}, " +
            $"rumble={DescribeCharacteristic(_rumbleCharacteristic)}.");
    }

    private async Task OpenGattSessionAsync(CancellationToken cancellationToken)
    {
        if (_device is null)
        {
            return;
        }

        try
        {
            _gattSession = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            _gattSession.SessionStatusChanged += OnGattSessionStatusChanged;
            _gattSession.MaxPduSizeChanged += OnGattSessionMaxPduSizeChanged;
            _gattSession.MaintainConnection = true;
            SetLinkDiagnostics($"GATT session {_gattSession.SessionStatus}, maintain=true, maxPdu={_gattSession.MaxPduSize}");
        }
        catch (Exception ex)
        {
            Trace?.Invoke(this, "BLE GATT session open failed: " + ex.Message);
        }
    }

    private void RequestLowLatencyConnectionParameters(string reason)
    {
        if (_device is null)
        {
            return;
        }

        try
        {
            var preferred = BluetoothLEPreferredConnectionParameters.ThroughputOptimized;
            var request = _device.RequestPreferredConnectionParameters(preferred);
            var oldRequest = _connectionParametersRequest;
            _connectionParametersRequest = request;
            oldRequest?.Dispose();
            SetLinkDiagnostics($"Preferred BLE params requested ({reason}): {Describe(preferred)}");
        }
        catch (Exception ex)
        {
            Trace?.Invoke(this, "BLE preferred connection parameters request failed: " + ex.Message);
        }
    }

    private async Task SubscribeCharacteristicAsync(
        GattCharacteristic? characteristic,
        string label,
        CancellationToken cancellationToken)
    {
        if (characteristic is null)
        {
            Trace?.Invoke(this, $"BLE {label} notify skipped; characteristic missing.");
            return;
        }

        characteristic.ValueChanged += OnValueChanged;
        var status = await characteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"GATT {label} notify subscribe failed: {status}");
        }

        Trace?.Invoke(this, $"BLE {label} notify subscribed.");
    }

    private async Task SendInitializationAsync(CancellationToken cancellationToken)
    {
        (string Name, byte[] Data)[] commands =
        [
            ("INIT", [0x03, 0x91, 0x01, 0x0d, 0x00, 0x08, 0x00, 0x00, 0x01, 0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]),
            ("CMD_07", [0x07, 0x91, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00]),
            ("CMD_16", [0x16, 0x91, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00]),
            ("CMD_15_03", [0x15, 0x91, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00]),
            ("FEATSEL_SET_MASK", [0x0c, 0x91, 0x01, 0x02, 0x00, 0x04, 0x00, 0x00, 0xff, 0x00, 0x00, 0x00]),
            ("CMD_11", [0x11, 0x91, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00]),
            ("VIBRATE_CFG", [0x0a, 0x91, 0x01, 0x08, 0x00, 0x14, 0x00, 0x00, 0x01, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x35, 0x00, 0x46, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]),
            ("FEATSEL_ENABLE", [0x0c, 0x91, 0x01, 0x04, 0x00, 0x04, 0x00, 0x00, 0xff, 0x00, 0x00, 0x00]),
            ("SELECT_REPORT", [0x03, 0x91, 0x01, 0x0a, 0x00, 0x04, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00]),
            ("FW_INFO_GET", [0x10, 0x91, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00]),
            ("CMD_01_0C", [0x01, 0x91, 0x01, 0x0c, 0x00, 0x00, 0x00, 0x00]),
            ("RUMBLE_ENABLE", [0x01, 0x91, 0x01, 0x01, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]),
            ("SET_PLAYER_LED", [0x09, 0x91, 0x01, 0x07, 0x00, 0x08, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]),
            ("CALIB_LEFT", [0x02, 0x91, 0x01, 0x04, 0x00, 0x08, 0x00, 0x00, 0x09, 0x7e, 0x00, 0x00, 0xa8, 0x30, 0x01, 0x00]),
            ("CALIB_RIGHT", [0x02, 0x91, 0x01, 0x04, 0x00, 0x08, 0x00, 0x00, 0x09, 0x7e, 0x00, 0x00, 0xe8, 0x30, 0x01, 0x00]),
        ];

        for (var i = 0; i < commands.Length; i++)
        {
            var command = commands[i];
            TaskCompletionSource<byte[]>? ackTcs = null;
            if (_ackCharacteristic is not null)
            {
                ackTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_ackLock)
                {
                    _pendingAck = ackTcs;
                }
            }

            Trace?.Invoke(this, $"BLE init {i + 1}/{commands.Length} {command.Name}: write {command.Data.Length} bytes.");
            var commandStarted = Stopwatch.GetTimestamp();
            await WriteCommandAsync(command.Data, cancellationToken).ConfigureAwait(false);

            if (ackTcs is not null)
            {
                var completed = await Task.WhenAny(ackTcs.Task, Task.Delay(250, cancellationToken)).ConfigureAwait(false);
                var elapsedMs = Stopwatch.GetElapsedTime(commandStarted).TotalMilliseconds;
                if (completed == ackTcs.Task && ackTcs.Task.IsCompletedSuccessfully)
                {
                    var ack = await ackTcs.Task.ConfigureAwait(false);
                    Trace?.Invoke(
                        this,
                        $"BLE init {command.Name}: ACK received in {elapsedMs:F1} ms, " +
                        $"length={ack.Length}, data={FormatHexPrefix(ack)}.");
                }
                else
                {
                    Trace?.Invoke(this, $"BLE init {command.Name}: ACK timeout after {elapsedMs:F1} ms.");
                }

                lock (_ackLock)
                {
                    if (ReferenceEquals(_pendingAck, ackTcs))
                    {
                        _pendingAck = null;
                    }
                }
            }
            else
            {
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteCommandAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_writeCharacteristic is null)
        {
            throw new InvalidOperationException("BLE command characteristic is not available.");
        }

        await WriteCharacteristicAsync(_writeCharacteristic, data, "command", cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteCharacteristicAsync(
        GattCharacteristic characteristic,
        ReadOnlyMemory<byte> data,
        string purpose,
        CancellationToken cancellationToken)
    {
        var writer = new DataWriter();
        writer.WriteBytes(data.ToArray());
        var option = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;
        var started = Stopwatch.GetTimestamp();
        var status = await characteristic
            .WriteValueAsync(writer.DetachBuffer(), option)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException(
                $"BLE {purpose} write failed: status={status}, option={option}, " +
                $"length={data.Length}, elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = BleScanner.ReadBytes(args.CharacteristicValue);
        var uuid = sender.Uuid.ToString();
        RecordNotificationDiagnostics(uuid, data.Length);
        if (uuid.Equals(AckReportUuid, StringComparison.OrdinalIgnoreCase))
        {
            lock (_ackLock)
            {
                _pendingAck?.TrySetResult(data);
            }
        }

        NotificationReceived?.Invoke(
            this,
            new BleNotificationEventArgs(uuid, data, args.Timestamp));
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        Trace?.Invoke(this, $"BLE device connection status changed: {sender.ConnectionStatus}.");
        ConnectionStatusChanged?.Invoke(this, sender.ConnectionStatus);
    }

    private void OnGattSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
    {
        SetLinkDiagnostics($"GATT session {args.Status}, error={args.Error}, maxPdu={sender.MaxPduSize}");
    }

    private void OnGattSessionMaxPduSizeChanged(GattSession sender, object args)
    {
        SetLinkDiagnostics($"GATT session {sender.SessionStatus}, maxPdu={sender.MaxPduSize}");
    }

    private void SetLinkDiagnostics(string message)
    {
        Trace?.Invoke(this, message);
        LinkDiagnosticsChanged?.Invoke(this, message);
    }

    private static string Describe(BluetoothLEPreferredConnectionParameters parameters)
    {
        var minMs = parameters.MinConnectionInterval * 1.25;
        var maxMs = parameters.MaxConnectionInterval * 1.25;
        var timeoutMs = parameters.LinkTimeout * 10;
        return $"min={parameters.MinConnectionInterval} ({minMs:F2}ms), max={parameters.MaxConnectionInterval} ({maxMs:F2}ms), latency={parameters.ConnectionLatency}, timeout={timeoutMs}ms";
    }

    private void RecordNotificationDiagnostics(string uuid, int dataLength)
    {
        string? message = null;
        lock (_notificationDiagnosticsLock)
        {
            var now = Stopwatch.GetTimestamp();
            if (uuid.Equals(InputReportUuid, StringComparison.OrdinalIgnoreCase) ||
                uuid.Equals(Fd2ReportParser.LegacyNotifyUuid, StringComparison.OrdinalIgnoreCase))
            {
                _inputNotifications++;
                _inputBytes += dataLength;
                _minimumInputSize = Math.Min(_minimumInputSize, dataLength);
                _maximumInputSize = Math.Max(_maximumInputSize, dataLength);
                if (_lastInputNotificationTicks > 0)
                {
                    var interval = now - _lastInputNotificationTicks;
                    _inputIntervalTicks += interval;
                    _inputIntervalSamples++;
                    _minimumInputIntervalTicks = Math.Min(_minimumInputIntervalTicks, interval);
                    _maximumInputIntervalTicks = Math.Max(_maximumInputIntervalTicks, interval);
                }

                _lastInputNotificationTicks = now;
            }
            else if (uuid.Equals(AckReportUuid, StringComparison.OrdinalIgnoreCase))
            {
                _ackNotifications++;
            }
            else
            {
                _otherNotifications++;
            }

            var windowTicks = now - _notificationWindowStartedTicks;
            if (windowTicks >= Stopwatch.Frequency * 5L)
            {
                var seconds = windowTicks / (double)Stopwatch.Frequency;
                var rate = _inputNotifications / seconds;
                var averageSize = _inputNotifications > 0 ? _inputBytes / (double)_inputNotifications : 0;
                var averageInterval = _inputIntervalSamples > 0
                    ? _inputIntervalTicks * 1000.0 / Stopwatch.Frequency / _inputIntervalSamples
                    : 0;
                var minimumInterval = _minimumInputIntervalTicks != long.MaxValue
                    ? _minimumInputIntervalTicks * 1000.0 / Stopwatch.Frequency
                    : 0;
                var maximumInterval = _maximumInputIntervalTicks * 1000.0 / Stopwatch.Frequency;
                var minimumSize = _minimumInputSize != int.MaxValue ? _minimumInputSize : 0;
                message =
                    $"BLE notification window {seconds:F1}s: input={_inputNotifications} ({rate:F1} Hz), " +
                    $"ack={_ackNotifications}, other={_otherNotifications}, " +
                    $"inputBytes={_inputBytes}, size[min/avg/max]={minimumSize}/{averageSize:F1}/{_maximumInputSize}, " +
                    $"intervalMs[min/avg/max]={minimumInterval:F2}/{averageInterval:F2}/{maximumInterval:F2}.";

                _notificationWindowStartedTicks = now;
                _inputNotifications = 0;
                _ackNotifications = 0;
                _otherNotifications = 0;
                _inputBytes = 0;
                _inputIntervalTicks = 0;
                _inputIntervalSamples = 0;
                _minimumInputIntervalTicks = long.MaxValue;
                _maximumInputIntervalTicks = 0;
                _minimumInputSize = int.MaxValue;
                _maximumInputSize = 0;
            }
        }

        if (message is not null)
        {
            Trace?.Invoke(this, message);
        }
    }

    private static string DescribeCharacteristic(GattCharacteristic? characteristic) =>
        characteristic is null
            ? "none"
            : $"{characteristic.Uuid} [{characteristic.CharacteristicProperties}]";

    private static string FormatHexPrefix(ReadOnlySpan<byte> data)
    {
        const int maximumBytes = 24;
        var prefix = Convert.ToHexString(data[..Math.Min(data.Length, maximumBytes)]);
        return data.Length > maximumBytes ? prefix + "..." : prefix;
    }
}

public sealed class BleNotificationEventArgs(
    string characteristicUuid,
    byte[] data,
    DateTimeOffset timestamp) : EventArgs
{
    public string CharacteristicUuid { get; } = characteristicUuid;
    public byte[] Data { get; } = data;
    public DateTimeOffset Timestamp { get; } = timestamp;
}

