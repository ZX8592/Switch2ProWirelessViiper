using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace Switch2ProWirelessViiper.Core;

public sealed class BleScanner
{
    private const ushort NintendoCompanyId = 0x0553;

    public async Task<IReadOnlyList<BleDeviceCandidate>> ScanAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<ulong, BleDeviceCandidate>();
        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        watcher.Received += (_, args) =>
        {
            if (!LooksLikeNintendoController(args))
            {
                return;
            }

            found[args.BluetoothAddress] = new BleDeviceCandidate(
                args.BluetoothAddress,
                string.IsNullOrWhiteSpace(args.Advertisement.LocalName)
                    ? null
                    : args.Advertisement.LocalName,
                args.RawSignalStrengthInDBm,
                args.Timestamp);
        };

        watcher.Start();
        try
        {
            await Task.Delay(duration, done.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            watcher.Stop();
        }

        return found.Values
            .OrderByDescending(candidate => candidate.Rssi)
            .ThenBy(candidate => candidate.BluetoothAddress)
            .ToArray();
    }

    public static byte[] ReadBytes(IBuffer buffer)
    {
        var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
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
}
