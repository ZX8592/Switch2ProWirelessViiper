using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace Switch2ProWirelessViiper.Core;

public sealed class BleScanner
{
    private const ushort NintendoCompanyId = 0x0553;

    public event EventHandler<string>? Trace;

    public async Task<IReadOnlyList<BleDeviceCandidate>> ScanAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await ValidateBluetoothEnvironmentAsync(cancellationToken).ConfigureAwait(false);

        var found = new ConcurrentDictionary<ulong, BleDeviceCandidate>();
        Exception? activeFailure = null;
        try
        {
            TraceMessage("Starting active BLE advertisement scan.");
            await ScanAdvertisementsAsync(
                    BluetoothLEScanningMode.Active,
                    duration,
                    found,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            activeFailure = ex;
            TraceMessage("Active BLE scan failed: " + DescribeException(ex));
            TraceMessage("Retrying with passive BLE scan.");
            try
            {
                await ScanAdvertisementsAsync(
                        BluetoothLEScanningMode.Passive,
                        duration,
                        found,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception passiveFailure)
            {
                TraceMessage("Passive BLE scan failed: " + DescribeException(passiveFailure));
                var known = await EnumerateKnownNintendoDevicesAsync(cancellationToken).ConfigureAwait(false);
                if (known.Count > 0)
                {
                    TraceMessage($"Advertisement scanning is unavailable; using {known.Count} known Nintendo BLE device(s).");
                    return known;
                }

                throw new InvalidOperationException(
                    "Windows could not start BLE scanning. " +
                    $"Active scan: {DescribeException(activeFailure)}; " +
                    $"passive scan: {DescribeException(passiveFailure)}. " +
                    "Check that Bluetooth is on, the adapter supports BLE, and the Windows Bluetooth service and privacy settings allow scanning.",
                    passiveFailure);
            }
        }

        if (found.IsEmpty)
        {
            var known = await EnumerateKnownNintendoDevicesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var candidate in known)
            {
                found[candidate.BluetoothAddress] = candidate;
            }
        }

        return found.Values
            .OrderByDescending(candidate => candidate.Rssi)
            .ThenBy(candidate => candidate.BluetoothAddress)
            .ToArray();
    }

    public Task LogEnvironmentAsync(CancellationToken cancellationToken) =>
        ValidateBluetoothEnvironmentAsync(cancellationToken);

    public static byte[] ReadBytes(IBuffer buffer)
    {
        var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task ValidateBluetoothEnvironmentAsync(CancellationToken cancellationToken)
    {
        BluetoothAdapter? adapter;
        try
        {
            adapter = await BluetoothAdapter.GetDefaultAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Windows could not access the Bluetooth adapter: " + DescribeException(ex),
                ex);
        }

        if (adapter is null)
        {
            throw new InvalidOperationException(
                "No Windows Bluetooth adapter is available. Enable the adapter in Device Manager or reconnect the USB Bluetooth adapter.");
        }

        if (!adapter.IsLowEnergySupported)
        {
            throw new InvalidOperationException(
                "The active Bluetooth adapter does not expose Bluetooth Low Energy support to Windows. Update its driver or use a BLE-capable adapter.");
        }

        TraceMessage(
            $"Bluetooth adapter capabilities: id='{adapter.DeviceId}', " +
            $"BLE={adapter.IsLowEnergySupported}, Classic={adapter.IsClassicSupported}, " +
            $"Central={adapter.IsCentralRoleSupported}, Peripheral={adapter.IsPeripheralRoleSupported}.");
        await TraceAdapterPropertiesAsync(adapter.DeviceId, cancellationToken).ConfigureAwait(false);

        try
        {
            var radio = await adapter.GetRadioAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (radio is not null && radio.State is RadioState.Off or RadioState.Disabled)
            {
                throw new InvalidOperationException(
                    radio.State == RadioState.Disabled
                        ? "The Bluetooth radio is disabled by Windows or hardware. Enable it before scanning."
                        : "Bluetooth is turned off. Turn it on before scanning.");
            }

            if (radio is not null)
            {
                TraceMessage($"Bluetooth radio: name='{radio.Name}', kind={radio.Kind}, state={radio.State}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            TraceMessage(
                "Bluetooth radio state access was denied; continuing scan because adapter BLE capability is available: " +
                DescribeException(ex));
        }
        catch (Exception ex)
        {
            TraceMessage("Bluetooth radio state could not be queried; continuing scan: " + DescribeException(ex));
        }
    }

    private async Task ScanAdvertisementsAsync(
        BluetoothLEScanningMode mode,
        TimeSpan duration,
        ConcurrentDictionary<ulong, BleDeviceCandidate> found,
        CancellationToken cancellationToken)
    {
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = mode,
        };
        var stopped = new TaskCompletionSource<BluetoothError>(TaskCreationOptions.RunContinuationsAsynchronously);
        long receivedCount = 0;
        long matchedCount = 0;

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            Interlocked.Increment(ref receivedCount);
            if (!LooksLikeNintendoController(args))
            {
                return;
            }

            Interlocked.Increment(ref matchedCount);

            found[args.BluetoothAddress] = new BleDeviceCandidate(
                args.BluetoothAddress,
                string.IsNullOrWhiteSpace(args.Advertisement.LocalName)
                    ? null
                    : args.Advertisement.LocalName,
                args.RawSignalStrengthInDBm,
                args.Timestamp);
        }

        void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args) =>
            stopped.TrySetResult(args.Error);

        watcher.Received += OnReceived;
        watcher.Stopped += OnStopped;
        try
        {
            try
            {
                watcher.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{mode} BLE watcher failed to start: {DescribeException(ex)}",
                    ex);
            }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(duration, delayCts.Token);
            var completed = await Task.WhenAny(delay, stopped.Task).ConfigureAwait(false);
            if (completed == stopped.Task)
            {
                delayCts.Cancel();
                var error = await stopped.Task.ConfigureAwait(false);
                if (error != BluetoothError.Success)
                {
                    throw new InvalidOperationException($"{mode} BLE watcher stopped with {error}.");
                }
            }
            else
            {
                await delay.ConfigureAwait(false);
            }
        }
        finally
        {
            watcher.Received -= OnReceived;
            watcher.Stopped -= OnStopped;
            try
            {
                if (watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started or
                    BluetoothLEAdvertisementWatcherStatus.Stopping)
                {
                    watcher.Stop();
                }
            }
            catch
            {
            }

            TraceMessage(
                $"{mode} BLE scan completed: watcherStatus={watcher.Status}, " +
                $"advertisements={Interlocked.Read(ref receivedCount)}, " +
                $"NintendoMatches={Interlocked.Read(ref matchedCount)}, uniqueCandidates={found.Count}.");
        }
    }

    private async Task TraceAdapterPropertiesAsync(string deviceId, CancellationToken cancellationToken)
    {
        string[] requestedProperties =
        [
            "System.Devices.FriendlyName",
            "System.Devices.Manufacturer",
            "System.Devices.ModelName",
            "System.Devices.DriverVersion",
            "System.Devices.DriverDate",
            "System.Devices.DeviceInstanceId",
        ];

        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(deviceId, requestedProperties)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (info is null)
            {
                TraceMessage("Bluetooth adapter DeviceInformation lookup returned no result.");
                return;
            }

            var properties = requestedProperties
                .Select(key => $"{key[(key.LastIndexOf('.') + 1)..]}={FormatProperty(info.Properties, key)}");
            TraceMessage($"Bluetooth adapter identity: name='{info.Name}', {string.Join(", ", properties)}.");
        }
        catch (Exception ex)
        {
            TraceMessage("Bluetooth adapter identity query failed: " + DescribeException(ex));
        }
    }

    private async Task<IReadOnlyList<BleDeviceCandidate>> EnumerateKnownNintendoDevicesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector())
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            var candidates = new Dictionary<ulong, BleDeviceCandidate>();
            foreach (var info in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BluetoothLEDevice? device = null;
                try
                {
                    device = await BluetoothLEDevice.FromIdAsync(info.Id)
                        .AsTask(cancellationToken)
                        .ConfigureAwait(false);
                    var name = device?.Name;
                    if (device is null || string.IsNullOrWhiteSpace(name) ||
                        !name.Contains("Nintendo", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    candidates[device.BluetoothAddress] = new BleDeviceCandidate(
                        device.BluetoothAddress,
                        name,
                        short.MinValue,
                        DateTimeOffset.UtcNow);
                }
                catch
                {
                }
                finally
                {
                    device?.Dispose();
                }
            }

            return candidates.Values.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TraceMessage("Known BLE device enumeration failed: " + DescribeException(ex));
            return [];
        }
    }

    private static bool LooksLikeNintendoController(BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (args.Advertisement.ManufacturerData.Any(data => data.CompanyId == NintendoCompanyId))
        {
            return true;
        }

        var name = args.Advertisement.LocalName;
        return !string.IsNullOrWhiteSpace(name) &&
               name.Contains("Nintendo", StringComparison.OrdinalIgnoreCase);
    }

    private void TraceMessage(string message) => Trace?.Invoke(this, message);

    private static string FormatProperty(IReadOnlyDictionary<string, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value) || value is null)
        {
            return "n/a";
        }

        return value switch
        {
            DateTimeOffset date => date.ToString("O"),
            Array array => string.Join(";", array.Cast<object>()),
            _ => value.ToString() ?? "n/a",
        };
    }

    private static string DescribeException(Exception? exception) =>
        exception is null
            ? "unknown error"
            : $"{exception.Message} (HRESULT 0x{exception.HResult:X8})";
}
