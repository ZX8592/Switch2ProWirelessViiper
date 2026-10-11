using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Switch2ProWirelessViiper.Core;

public enum UsbipFailureKind
{
    None,
    ExecutableNotFound,
    MultipleVhciInterfaces,
    AbiMismatch,
    DriverUnavailable,
    ProbeFailed,
}

public sealed record UsbipEnvironmentStatus(
    string? UsbipExePath,
    bool DriverPackagePresent,
    string Details,
    bool UsbipExeCompatible,
    UsbipFailureKind FailureKind = UsbipFailureKind.None)
{
    public bool IsReady => DriverPackagePresent && UsbipExePath is not null && UsbipExeCompatible && FailureKind == UsbipFailureKind.None;
}

public static class UsbipVirtualController
{
    private const string ControllerHardwareId = "VID_057E&PID_2069";
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint DnStarted = 0x00000008;
    private const uint DnHasProblem = 0x00000400;
    private const uint DigcfPresent = 0x00000002;
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
            executable.Compatible,
            executable.FailureKind);
    }

    public static async Task<string> EnsureAttachedAsync(
        string host,
        uint busId,
        string deviceId,
        Action<string>? trace,
        CancellationToken cancellationToken,
        int usbipPort = 3241,
        bool allowExistingDevice = true)
    {
        var beforeAttach = EnumerateControllerHidInterfaces();
        var instanceId = beforeAttach.FirstOrDefault(device => device.Ready)?.InstanceId;
        if (allowExistingDevice && instanceId is not null)
        {
            trace?.Invoke("Virtual Switch 2 Pro game-controller HID interface is ready: " + instanceId);
            return instanceId;
        }

        var environment = await InspectAsync(cancellationToken).ConfigureAwait(false);
        trace?.Invoke("USBIP environment: " + environment.Details);
        if (environment.UsbipExePath is null || !environment.UsbipExeCompatible)
        {
            throw new InvalidOperationException(DescribeEnvironmentFailure(environment));
        }

        var exportedBusId = $"{busId}-{deviceId}";
        trace?.Invoke($"Attaching USBIP bus {exportedBusId} manually from {host}:{usbipPort}; waiting for a started game-controller HID interface.");
        var attach = await RunProcessAsync(
                environment.UsbipExePath,
                $"--tcp-port {usbipPort} attach --remote {host} --bus-id {exportedBusId}",
                TimeSpan.FromSeconds(15),
                cancellationToken)
            .ConfigureAwait(false);
        trace?.Invoke($"usbip attach exited with code {attach.ExitCode}: {attach.Summary}");
        if (attach.ExitCode != 0)
        {
            ThrowIfKnownDriverFailure(attach);
            trace?.Invoke("Retrying usbip attach with legacy argument order.");
            attach = await RunProcessAsync(
                    environment.UsbipExePath,
                    $"--tcp-port {usbipPort} attach -r {host} --bus-id {exportedBusId}",
                    TimeSpan.FromSeconds(15),
                    cancellationToken)
                .ConfigureAwait(false);
            trace?.Invoke($"usbip attach retry exited with code {attach.ExitCode}: {attach.Summary}");
        }

        if (attach.ExitCode != 0)
        {
            ThrowIfKnownDriverFailure(attach);
            throw new InvalidOperationException($"USBIP attach failed with exit code {attach.ExitCode}: {attach.Summary}. No virtual HID readiness was confirmed.");
        }

        var excludedInterfaces = allowExistingDevice
            ? null
            : beforeAttach.Where(device => device.Ready).Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        instanceId = await WaitForVirtualControllerAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken,
                excludedInterfaces,
                trace)
            .ConfigureAwait(false);
        if (instanceId is null)
        {
            var driverHint = environment.DriverPackagePresent
                ? string.Empty
                : " The usbip-win2 driver package was not detected.";
            throw new InvalidOperationException(
                "USBIP attach succeeded, but Windows did not expose a new, started Switch 2 Pro game-controller HID interface. " +
                "A USB parent node with VID_057E&PID_2069 alone is not sufficient. " +
                $"usbip attach exit code: {attach.ExitCode}. {attach.Summary}.{driverHint} " +
                "Check the HID child device and its problem code in Device Manager; see HID readiness diagnostics above.");
        }

        trace?.Invoke("Virtual Switch 2 Pro game-controller HID interface attached and ready: " + instanceId);
        return instanceId;
    }

    public static string? FindVirtualControllerInstanceId()
        => EnumerateControllerHidInterfaces().FirstOrDefault(device => device.Ready)?.InstanceId;

    private static List<HidInterfaceStatus> EnumerateControllerHidInterfaces()
    {
        var devices = new List<HidInterfaceStatus>();
        HidD_GetHidGuid(out var hidGuid);
        var deviceInfoSet = SetupDiGetClassDevs(
            ref hidGuid,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == InvalidHandleValue)
        {
            return devices;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>(),
                };
                if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems)
                    {
                        break;
                    }

                    break;
                }

                var deviceInfo = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, ref deviceInfo);
                if (requiredSize == 0) continue;
                var detail = Marshal.AllocHGlobal(checked((int)requiredSize));
                string? devicePath;
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detail, requiredSize, out _, ref deviceInfo)) continue;
                    devicePath = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
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
                if (!value.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) ||
                    !value.Contains(ControllerHardwareId, StringComparison.OrdinalIgnoreCase) || devicePath is null)
                {
                    continue;
                }

                var result = CM_Get_DevNode_Status(out var status, out var problem, deviceInfo.DevInst, 0);
                var ready = false;
                var reason = $"device status query failed: CR=0x{result:X}";
                if (result == 0)
                {
                    reason = $"status=0x{status:X}, problem={problem}";
                    if (IsStartedControllerHid(value, status, problem))
                    {
                        ready = ProbeControllerHid(devicePath, out reason);
                    }
                }

                devices.Add(new HidInterfaceStatus(value, ready, reason));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return devices;
    }

    private static bool ProbeControllerHid(string devicePath, out string details)
    {
        using var handle = CreateFile(devicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            details = $"HID interface open failed: Win32={Marshal.GetLastWin32Error()}";
            return false;
        }

        var attributes = new HidAttributes { Size = (uint)Marshal.SizeOf<HidAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != 0x057E || attributes.ProductId != 0x2069)
        {
            details = "HID attributes unavailable or VID/PID mismatch";
            return false;
        }

        if (!HidD_GetPreparsedData(handle, out var preparsed))
        {
            details = $"HID report descriptor unavailable: Win32={Marshal.GetLastWin32Error()}";
            return false;
        }

        try
        {
            var capsResult = HidP_GetCaps(preparsed, out var caps);
            details = $"HID caps: status=0x{capsResult:X8}, usagePage=0x{caps.UsagePage:X}, usage=0x{caps.Usage:X}, inputReportBytes={caps.InputReportByteLength}";
            return capsResult == 0x00110000 && IsGameControllerReport(caps.UsagePage, caps.Usage, caps.InputReportByteLength);
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    internal static bool IsGameControllerReport(ushort usagePage, ushort usage, ushort inputReportBytes)
        => usagePage == 0x01 && usage is 0x04 or 0x05 && inputReportBytes > 1;

    internal static bool IsStartedControllerHid(string instanceId, uint status, uint problem)
        => instanceId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) &&
           instanceId.Contains(ControllerHardwareId, StringComparison.OrdinalIgnoreCase) &&
           (status & DnStarted) != 0 && (status & DnHasProblem) == 0 && problem == 0;

    private static async Task<string?> WaitForVirtualControllerAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        ISet<string>? excludedInterfaces,
        Action<string>? trace)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        string? lastDiagnostics = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var interfaces = EnumerateControllerHidInterfaces();
            var instanceId = interfaces.FirstOrDefault(device => device.Ready && excludedInterfaces?.Contains(device.InstanceId) != true)?.InstanceId;
            if (instanceId is not null)
            {
                return instanceId;
            }

            var diagnostics = interfaces.Count == 0
                ? "No matching HID interface is present (a matching USB parent alone is not ready)."
                : string.Join("; ", interfaces.Select(device => $"{device.InstanceId}: ready={device.Ready}, preExisting={excludedInterfaces?.Contains(device.InstanceId) == true}, {device.Details}"));
            if (diagnostics != lastDiagnostics)
            {
                trace?.Invoke("HID readiness: " + diagnostics);
                lastDiagnostics = diagnostics;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return EnumerateControllerHidInterfaces().FirstOrDefault(device => device.Ready && excludedInterfaces?.Contains(device.InstanceId) != true)?.InstanceId;
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
            return new UsbipExecutableProbe(null, false, "usbip.exe was not found; manual USBIP attach is unavailable", UsbipFailureKind.ExecutableNotFound);
        }

        var rejected = new List<UsbipExecutableProbe>();
        foreach (var candidate in candidates)
        {
            var probe = await ProbeUsbipExeAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (probe.Compatible)
            {
                return probe;
            }

            rejected.Add(probe);
        }

        var details = string.Join("; ", rejected.Take(5).Select(probe => probe.Details));
        if (rejected.Count > 5)
        {
            details += $"; {rejected.Count - 5} more candidate(s) skipped";
        }

        var failureKind = rejected.Any(probe => probe.FailureKind == UsbipFailureKind.MultipleVhciInterfaces)
            ? UsbipFailureKind.MultipleVhciInterfaces
            : rejected.All(probe => probe.FailureKind == UsbipFailureKind.AbiMismatch)
                ? UsbipFailureKind.AbiMismatch
                : rejected.All(probe => probe.FailureKind == UsbipFailureKind.DriverUnavailable)
                    ? UsbipFailureKind.DriverUnavailable
                    : UsbipFailureKind.ProbeFailed;
        return new UsbipExecutableProbe(
            candidates[0],
            false,
            "usbip.exe was found, but no candidate passed the client/driver probe. " + details,
            failureKind);
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

            var failure = ClassifyFailure(result.CombinedOutput);
            var reason = failure == UsbipFailureKind.AbiMismatch
                ? "ABI mismatch with installed usbip-win2 driver"
                : $"probe exited with code {result.ExitCode}: {summary}";
            return new UsbipExecutableProbe(path, false, $"{description}, {reason}", failure);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UsbipExecutableProbe(path, false, $"{description}, probe failed: {DescribeException(ex)}", UsbipFailureKind.ProbeFailed);
        }
    }

    internal static UsbipFailureKind ClassifyFailure(string message)
    {
        if (message.Contains("Multiple instances of VHCI device interface", StringComparison.OrdinalIgnoreCase))
            return UsbipFailureKind.MultipleVhciInterfaces;
        if (message.Contains("ABI mismatch", StringComparison.OrdinalIgnoreCase))
            return UsbipFailureKind.AbiMismatch;
        if (message.Contains("VHCI device interface not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No VHCI device interface", StringComparison.OrdinalIgnoreCase))
            return UsbipFailureKind.DriverUnavailable;
        if (message.Contains("executable file not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not found in %PATH%", StringComparison.OrdinalIgnoreCase))
            return UsbipFailureKind.ExecutableNotFound;
        return UsbipFailureKind.ProbeFailed;
    }

    internal static string DescribeEnvironmentFailure(UsbipEnvironmentStatus environment)
    {
        var reason = environment.FailureKind switch
        {
            UsbipFailureKind.MultipleVhciInterfaces =>
                "Multiple USBIP VHCI host-controller interfaces were found. usbip.exe exists, but the client cannot select a unique host controller. " +
                "Check for duplicate active USBIP host-controller instances in Device Manager. Changing usbip.exe versions is not the indicated fix.",
            UsbipFailureKind.ExecutableNotFound =>
                "usbip.exe was not found in the app folder, registered install locations, or PATH. Locate the installed client executable; this does not by itself mean the driver is missing.",
            UsbipFailureKind.AbiMismatch =>
                "The USBIP client explicitly reported an ABI mismatch with the installed driver. Use a client and driver from the same usbip-win2 release.",
            UsbipFailureKind.DriverUnavailable =>
                "usbip.exe was found, but no active VHCI host-controller interface was found. Check the driver's active device and problem code in Device Manager.",
            _ =>
                "usbip.exe was found, but its client/driver probe failed. Compatibility is unconfirmed; the probe's actual error is shown below."
        };
        return $"VIIPER virtual output could not be attached. {reason} Details: {environment.Details}";
    }

    private static void ThrowIfKnownDriverFailure(ProcessResult result)
    {
        var failure = ClassifyFailure(result.CombinedOutput);
        if (failure is UsbipFailureKind.MultipleVhciInterfaces or UsbipFailureKind.AbiMismatch or UsbipFailureKind.DriverUnavailable)
        {
            throw new InvalidOperationException(DescribeEnvironmentFailure(
                new UsbipEnvironmentStatus(null, true, result.CombinedOutput, false, failure)));
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
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(fileName)} timed out after {timeout.TotalSeconds:0} seconds.");
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static string DescribeException(Exception exception) =>
        $"{exception.Message} (HRESULT 0x{exception.HResult:X8})";

    private sealed record UsbipExecutableProbe(string? Path, bool Compatible, string Details,
        UsbipFailureKind FailureKind = UsbipFailureKind.None);

    private sealed record HidInterfaceStatus(string InstanceId, bool Ready, string Details);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public uint Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HidCaps
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort UsagePage;
        [FieldOffset(4)] public ushort InputReportByteLength;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr detailData,
        uint detailDataSize,
        out uint requiredSize,
        ref SpDevInfoData deviceInfoData);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HidAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidCaps capabilities);

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
