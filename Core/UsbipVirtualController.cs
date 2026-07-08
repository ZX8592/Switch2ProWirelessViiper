using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Switch2ProWirelessViiper.Core;

public sealed record UsbipEnvironmentStatus(
    string? UsbipExePath,
    bool DriverPackagePresent,
    string Details,
    bool UsbipExeCompatible)
{
    public bool IsReady => DriverPackagePresent && UsbipExePath is not null && UsbipExeCompatible;
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
        var executable = await ResolveCompatibleUsbipExeAsync(cancellationToken).ConfigureAwait(false);
        var usbipExe = executable.Path;
        var executableDetails = executable.Details;

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
            var driverPackageSummary = SummarizeUsbipDriverPackages(output);
            driverPresent = output.Contains("usbip", StringComparison.OrdinalIgnoreCase) ||
                            output.Contains("vhci", StringComparison.OrdinalIgnoreCase) ||
                            IsUsbipDriverServiceRegistered();
            driverDetails = !string.IsNullOrWhiteSpace(driverPackageSummary)
                ? $"usbip-win2 driver package detected: {driverPackageSummary}"
                : driverPresent
                    ? "usbip-win2 driver service detected"
                    : "usbip-win2 driver package was not detected";
        }
        catch (Exception ex)
        {
            driverPresent = IsUsbipDriverServiceRegistered();
            driverDetails = driverPresent
                ? "usbip-win2 driver service detected"
                : "driver check failed: " + DescribeException(ex);
        }

        return new UsbipEnvironmentStatus(
            usbipExe,
            driverPresent,
            $"{driverDetails}; {executableDetails}",
            executable.Compatible);
    }

    public static async Task<string> EnsureAttachedAsync(
        string host,
        uint busId,
        string deviceId,
        Action<string>? trace,
        CancellationToken cancellationToken)
    {
        var instanceId = FindVirtualControllerInstanceId();
        if (instanceId is not null)
        {
            trace?.Invoke("Virtual Switch 2 Pro USB device is present: " + instanceId);
            return instanceId;
        }

        var environment = await InspectAsync(cancellationToken).ConfigureAwait(false);
        trace?.Invoke("USBIP environment: " + environment.Details);
        if (environment.UsbipExePath is null || !environment.UsbipExeCompatible)
        {
            throw new InvalidOperationException(
                "VIIPER created the virtual controller, but Windows did not enumerate it. " +
                "This app disables VIIPER native auto-attach for usbip-win2 compatibility, " +
                "but a compatible usbip.exe was not found for manual attach. " +
                environment.Details + " " +
                "Reinstall usbip-win2, make sure usbip.exe and the driver come from the same version, then reboot Windows.");
        }

        var exportedBusId = $"{busId}-{deviceId}";
        trace?.Invoke($"VIIPER auto-attach did not produce a Windows HID device; attaching USBIP bus {exportedBusId} manually.");
        var attach = await RunProcessAsync(
                environment.UsbipExePath,
                $"--tcp-port {ViiperUsbipPort} attach --remote {host} --bus-id {exportedBusId}",
                TimeSpan.FromSeconds(15),
                cancellationToken)
            .ConfigureAwait(false);
        trace?.Invoke($"usbip attach exited with code {attach.ExitCode}: {attach.Summary}");
        if (attach.ExitCode != 0)
        {
            trace?.Invoke("Retrying usbip attach with legacy argument order.");
            attach = await RunProcessAsync(
                    environment.UsbipExePath,
                    $"--tcp-port {ViiperUsbipPort} attach -r {host} --bus-id {exportedBusId}",
                    TimeSpan.FromSeconds(15),
                    cancellationToken)
                .ConfigureAwait(false);
            trace?.Invoke($"usbip attach retry exited with code {attach.ExitCode}: {attach.Summary}");
        }

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

    private static async Task<UsbipExecutableProbe> ResolveCompatibleUsbipExeAsync(CancellationToken cancellationToken)
    {
        var candidates = BuildUsbipExeCandidates()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try { return Path.GetFullPath(path!); }
                catch { return string.Empty; }
            })
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new UsbipExecutableProbe(null, false, "usbip.exe was not found; manual USBIP attach is unavailable");
        }

        var rejected = new List<string>();
        foreach (var candidate in candidates)
        {
            var probe = await ProbeUsbipExeAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (probe.Compatible)
            {
                return probe;
            }

            rejected.Add(probe.Details);
        }

        var details = string.Join("; ", rejected.Take(5));
        if (rejected.Count > 5)
        {
            details += $"; {rejected.Count - 5} more candidate(s) skipped";
        }

        return new UsbipExecutableProbe(
            null,
            false,
            "No compatible usbip.exe was found. " + details);
    }

    private static async Task<UsbipExecutableProbe> ProbeUsbipExeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var description = DescribeUsbipExe(path);
        try
        {
            var result = await RunProcessAsync(
                    path,
                    "port",
                    TimeSpan.FromSeconds(5),
                    cancellationToken)
                .ConfigureAwait(false);
            var summary = result.Summary;
            if (result.ExitCode == 0)
            {
                return new UsbipExecutableProbe(path, true, $"{description}, ABI check passed");
            }

            var reason = summary.Contains("ABI mismatch", StringComparison.OrdinalIgnoreCase)
                ? "ABI mismatch with installed usbip-win2 driver"
                : $"probe exited with code {result.ExitCode}: {summary}";
            return new UsbipExecutableProbe(null, false, $"{description}, skipped: {reason}");
        }
        catch (Exception ex)
        {
            return new UsbipExecutableProbe(null, false, $"{description}, skipped: probe failed: {DescribeException(ex)}");
        }
    }

    private static IEnumerable<string?> BuildUsbipExeCandidates()
    {
        var candidates = new List<string?>
        {
            Path.Combine(AppContext.BaseDirectory, "usbip.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbip-win2", "usbip.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "usbip-win2", "usbip.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBIP", "usbip.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "USBIP", "usbip.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "usbip.exe"),
        };

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "usbip.exe"));
        }

        foreach (var directory in GetUsbipInstallDirectoriesFromRegistry())
        {
            candidates.Add(Path.Combine(directory, "usbip.exe"));
            candidates.Add(Path.Combine(directory, "bin", "usbip.exe"));
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = directory.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                candidates.Add(Path.Combine(normalized, "usbip.exe"));
            }
        }

        return candidates;
    }

    private static IEnumerable<string> GetUsbipInstallDirectoriesFromRegistry()
    {
        const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var viewPath in new[] { uninstallPath, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using var uninstall = root.OpenSubKey(viewPath);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var subKey = uninstall.OpenSubKey(subKeyName);
                    var displayName = subKey?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName) ||
                        !displayName.Contains("usbip", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var valueName in new[] { "InstallLocation", "InstallSource" })
                    {
                        var directory = subKey?.GetValue(valueName) as string;
                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            yield return directory.Trim().Trim('"');
                        }
                    }
                }
            }
        }
    }

    private static string DescribeUsbipExe(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            var versionText = !string.IsNullOrWhiteSpace(version.ProductVersion)
                ? version.ProductVersion
                : version.FileVersion;
            return string.IsNullOrWhiteSpace(versionText)
                ? $"usbip.exe: {path}"
                : $"usbip.exe: {path}, version {versionText}";
        }
        catch
        {
            return $"usbip.exe: {path}";
        }
    }

    private static string SummarizeUsbipDriverPackages(string output)
    {
        var blocks = output
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Where(block => block.Contains("usbip", StringComparison.OrdinalIgnoreCase) ||
                            block.Contains("vhci", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(SummarizeDriverPackageBlock)
            .Where(block => !string.IsNullOrWhiteSpace(block))
            .ToArray();

        if (blocks.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(" | ", blocks);
    }

    private static string SummarizeDriverPackageBlock(string block)
    {
        var compact = block.Replace("\r", " ").Replace("\n", " ").Trim();
        var published = Regex.Match(compact, @"oem\d+\.inf", RegexOptions.IgnoreCase).Value;
        var original = Regex.Match(compact, @"usbip[^\s:;|]*\.inf", RegexOptions.IgnoreCase).Value;
        var version = Regex.Match(compact, @"\d{2}/\d{2}/\d{4}\s+\d+(?:\.\d+)+", RegexOptions.IgnoreCase).Value;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(published)) parts.Add($"published={published}");
        if (!string.IsNullOrWhiteSpace(original)) parts.Add($"original={original}");
        if (!string.IsNullOrWhiteSpace(version)) parts.Add($"driverVersion={version}");
        return parts.Count == 0 ? "usbip/vhci driver package detected" : string.Join(", ", parts);
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
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
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

    private sealed record UsbipExecutableProbe(string? Path, bool Compatible, string Details);

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
