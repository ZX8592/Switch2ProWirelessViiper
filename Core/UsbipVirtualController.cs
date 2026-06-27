using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Switch2ProWirelessViiper.Core;

public sealed record UsbipEnvironmentStatus(
    string? UsbipExePath,
    bool DriverPackagePresent,
    string Details)
{
    public bool IsReady => DriverPackagePresent;
}

public static class UsbipVirtualController
{
    private const string ControllerHardwareId = "VID_057E&PID_2069";
    private const int ViiperUsbipPort = 3241;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const int ErrorNoMoreItems = 259;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static async Task<UsbipEnvironmentStatus> InspectAsync(CancellationToken cancellationToken)
    {
        var usbipExe = ResolveUsbipExe();
        var driverPresent = false;
        string driverDetails;
        try
        {
            var result = await RunProcessAsync(
                    "pnputil.exe",
                    "/enum-drivers",
                    TimeSpan.FromSeconds(8),
                    cancellationToken)
                .ConfigureAwait(false);
            var output = result.CombinedOutput;
            driverPresent = output.Contains("usbip", StringComparison.OrdinalIgnoreCase) ||
                            output.Contains("vhci", StringComparison.OrdinalIgnoreCase) ||
                            IsUsbipDriverServiceRegistered();
            driverDetails = driverPresent
                ? "usbip-win2 driver package detected"
                : "usbip-win2 driver package was not detected";
        }
        catch (Exception ex)
        {
            driverPresent = IsUsbipDriverServiceRegistered();
            driverDetails = driverPresent
                ? "usbip-win2 driver service detected"
                : "driver check failed: " + DescribeException(ex);
        }

        var executableDetails = usbipExe is null
            ? "usbip.exe was not found; VIIPER native auto-attach can still work, manual usbip fallback is unavailable"
            : $"usbip.exe: {usbipExe}";
        return new UsbipEnvironmentStatus(
            usbipExe,
            driverPresent,
            $"{driverDetails}; {executableDetails}");
    }

    public static async Task<string> EnsureAttachedAsync(
        string host,
        uint busId,
        string deviceId,
        Action<string>? trace,
        CancellationToken cancellationToken)
    {
        var instanceId = await WaitForVirtualControllerAsync(
                TimeSpan.FromSeconds(3),
                cancellationToken)
            .ConfigureAwait(false);
        if (instanceId is not null)
        {
            trace?.Invoke("Virtual Switch 2 Pro USB device is present: " + instanceId);
            return instanceId;
        }

        var environment = await InspectAsync(cancellationToken).ConfigureAwait(false);
        trace?.Invoke("USBIP environment: " + environment.Details);
        if (environment.UsbipExePath is null)
        {
            throw new InvalidOperationException(
                "VIIPER created the virtual controller, but Windows did not enumerate it. " +
                "VIIPER native auto-attach did not complete, and usbip.exe was not found for manual fallback. " +
                "Reinstall usbip-win2, reboot Windows, or add usbip.exe to PATH.");
        }

        var exportedBusId = $"{busId}-{deviceId}";
        trace?.Invoke($"VIIPER auto-attach did not produce a Windows HID device; attaching USBIP bus {exportedBusId} manually.");
        var attach = await RunProcessAsync(
                environment.UsbipExePath,
                $"attach --remote {host} --tcp-port {ViiperUsbipPort} --busid {exportedBusId}",
                TimeSpan.FromSeconds(15),
                cancellationToken)
            .ConfigureAwait(false);
        trace?.Invoke($"usbip attach exited with code {attach.ExitCode}: {attach.Summary}");

        instanceId = await WaitForVirtualControllerAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken)
            .ConfigureAwait(false);
        if (instanceId is null)
        {
            var driverHint = environment.DriverPackagePresent
                ? string.Empty
                : " The usbip-win2 driver package was not detected.";
            throw new InvalidOperationException(
                "VIIPER is running and the ns2pro stream opened, but Windows did not enumerate VID_057E&PID_2069. " +
                $"usbip attach exit code: {attach.ExitCode}. {attach.Summary}.{driverHint} " +
                "Reinstall usbip-win2, reboot Windows, and check Device Manager for a USBIP/VHCI error.");
        }

        trace?.Invoke("Virtual Switch 2 Pro USB device attached: " + instanceId);
        return instanceId;
    }

    public static string? FindVirtualControllerInstanceId()
    {
        var deviceInfoSet = SetupDiGetClassDevs(
            IntPtr.Zero,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfAllClasses);
        if (deviceInfoSet == InvalidHandleValue)
        {
            return null;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var deviceInfo = new SpDevInfoData
                {
                    CbSize = (uint)Marshal.SizeOf<SpDevInfoData>(),
                };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems)
                    {
                        break;
                    }

                    continue;
                }

                var instanceId = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(
                        deviceInfoSet,
                        ref deviceInfo,
                        instanceId,
                        instanceId.Capacity,
                        out _))
                {
                    continue;
                }

                var value = instanceId.ToString();
                if (value.Contains(ControllerHardwareId, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return null;
    }

    private static async Task<string?> WaitForVirtualControllerAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instanceId = FindVirtualControllerInstanceId();
            if (instanceId is not null)
            {
                return instanceId;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return FindVirtualControllerInstanceId();
    }

    private static bool IsUsbipDriverServiceRegistered()
    {
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null)
            {
                return false;
            }

            foreach (var name in services.GetSubKeyNames())
            {
                if (name.Contains("usbip", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("vhci", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static string? ResolveUsbipExe()
    {
        var candidates = new List<string?>
        {
            Path.Combine(AppContext.BaseDirectory, "usbip.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbip-win2", "usbip.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBIP", "usbip.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "usbip.exe"),
        };

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = directory.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                candidates.Add(Path.Combine(normalized, "usbip.exe"));
            }
        }

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate!);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {fileName}.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(fileName)} timed out after {timeout.TotalSeconds:0} seconds.");
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static string DescribeException(Exception exception) =>
        $"{exception.Message} (HRESULT 0x{exception.HResult:X8})";

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => $"{StandardOutput}{Environment.NewLine}{StandardError}".Trim();

        public string Summary
        {
            get
            {
                var value = CombinedOutput.Replace(Environment.NewLine, " ").Trim();
                return string.IsNullOrWhiteSpace(value)
                    ? "no output"
                    : value.Length <= 600 ? value : value[..600];
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
