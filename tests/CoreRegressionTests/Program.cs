using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Switch2ProWirelessViiper.Core;

if (args.FirstOrDefault() == "server")
{
    var apiAddress = args.Single(arg => arg.StartsWith("--api.addr="))["--api.addr=".Length..];
    var usbAddress = args.Single(arg => arg.StartsWith("--usb.addr="))["--usb.addr=".Length..];
    if (!args.Contains("--api.auto-attach-local-client=false") ||
        Environment.GetEnvironmentVariable("VIIPER_API_AUTO_ATTACH_LOCAL_CLIENT") != "false") return 20;
    await using var fake = new FakeServer(ParsePort(apiAddress), ParsePort(usbAddress), $"managed-{Environment.ProcessId}");
    Console.WriteLine($"Fake VIIPER listening: pid={Environment.ProcessId}, API={apiAddress}, USBIP={usbAddress}");
    await fake.Completion;
    return 0;
}

if (args.FirstOrDefault() == "owner-exit-test")
{
    var warmup = await ViiperBridge.WarmupServerAsync("localhost:3242", Environment.ProcessPath, CancellationToken.None);
    Console.WriteLine(warmup.Description);
    // Deliberately leave the lease unreleased to exercise process-exit cleanup.
    return 0;
}

var failures = 0;
await Run("Distinct USBIP failure categories", () =>
{
    Equal(UsbipFailureKind.MultipleVhciInterfaces, UsbipVirtualController.ClassifyFailure("error: Multiple instances of VHCI device interface found."));
    Equal(UsbipFailureKind.AbiMismatch, UsbipVirtualController.ClassifyFailure("error: ABI mismatch"));
    Equal(UsbipFailureKind.DriverUnavailable, UsbipVirtualController.ClassifyFailure("VHCI device interface not found"));
    Equal(UsbipFailureKind.ProbeFailed, UsbipVirtualController.ClassifyFailure("error: access denied"));
    Equal(UsbipFailureKind.ExecutableNotFound, UsbipVirtualController.ClassifyFailure("exec: usbip: executable file not found in %PATH%"));
    var status = new UsbipEnvironmentStatus("C:\\USBip\\usbip.exe", true, "Multiple instances of VHCI device interface found.", false, UsbipFailureKind.MultipleVhciInterfaces);
    var message = UsbipVirtualController.DescribeEnvironmentFailure(status);
    Check(message.Contains("host-controller") && !message.Contains("Reinstall") && !message.Contains("compatible usbip.exe was not found"), message);
    Check(!status.IsReady, "Conflicting VHCI instances must not be ready");
    var missing = ViiperBridge.DescribeAutoAttachFailure(new InvalidOperationException("409 Conflict: Failed to auto-attach device: exec: usbip: executable file not found in %PATH%"));
    Check(missing.Contains("process PATH") && !missing.Contains("usually caused") && !missing.Contains("old usbip"), missing);
    return Task.CompletedTask;
});

await Run("Only started controller HID children can be ready", () =>
{
    Check(!UsbipVirtualController.IsStartedControllerHid("USB\\VID_057E&PID_2069\\00", 8, 0), "USB parent accepted");
    Check(UsbipVirtualController.IsStartedControllerHid("HID\\VID_057E&PID_2069\\test", 8, 0), "Started HID child rejected");
    Check(!UsbipVirtualController.IsStartedControllerHid("HID\\VID_057E&PID_2069\\test", 0, 0), "Stopped HID accepted");
    Check(!UsbipVirtualController.IsStartedControllerHid("HID\\VID_057E&PID_2069\\test", 0x408, 0), "HID problem flag accepted");
    Check(!UsbipVirtualController.IsStartedControllerHid("HID\\VID_057E&PID_2069\\test", 8, 10), "HID problem code accepted");
    Check(UsbipVirtualController.IsGameControllerReport(1, 5, 64), "Gamepad report rejected");
    Check(UsbipVirtualController.IsGameControllerReport(1, 4, 64), "Joystick report rejected");
    Check(!UsbipVirtualController.IsGameControllerReport(1, 6, 64), "Keyboard report accepted");
    Check(!UsbipVirtualController.IsGameControllerReport(0xFF00, 5, 64), "Vendor-defined report accepted");
    Check(!UsbipVirtualController.IsGameControllerReport(1, 5, 0), "Missing input report accepted");
    var present = UsbipVirtualController.FindVirtualControllerInstanceId();
    Check(present is null || present.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase), "Native enumeration returned a USB parent");
    return Task.CompletedTask;
});

await Run("Managed warmup shares ownership and avoids occupied ports", async () =>
{
    var leases = new List<ViiperServerWarmup>();
    await using var outside = TryCreateExternal();
    var log = new ConcurrentQueue<string>();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var executable = Environment.ProcessPath!;
    try
    {
        var acquired = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            ViiperBridge.WarmupServerAsync("localhost:3242", executable, timeout.Token, log.Enqueue)));
        leases.AddRange(acquired);
        Check(acquired.All(item => item.Description == acquired[0].Description), "Warmup spawned different instances");
        Check(log.Count(line => line.StartsWith("Managed VIIPER started:")) == 1, "Concurrent warmup started multiple processes");
        var port = ParsePort(Regex.Match(acquired[0].Description, @"127\.0\.0\.1:\d+").Value);
        var version = await Ping(port);
        Check(version.StartsWith("managed-"), "Adopted an external server");
        if (outside is not null)
        {
            Check(port != 3242, "Did not isolate default-port conflict");
            Equal(0, outside.Requests.Count);
            Equal("external", await Ping(3242));
        }

        try
        {
            await ViiperBridge.ConnectAsync("127.0.0.1:3242", executable, timeout.Token, log.Enqueue);
            throw new Exception("Fake add failure unexpectedly succeeded");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("test-add-failure")) { }
        Equal(version, await Ping(port));
        Check(log.Count(line => line.StartsWith("Managed VIIPER started:")) == 1, "Connect did not reuse warmup");
        await acquired[0].DisposeAsync();
        Equal(version, await Ping(port));
        await acquired[0].DisposeAsync();
        Equal(version, await Ping(port));
        foreach (var lease in leases) await lease.DisposeAsync();
        leases.Clear();
        await CheckStopped(int.Parse(version["managed-".Length..]));
        if (outside is not null) Equal("external", await Ping(3242));
    }
    catch
    {
        foreach (var line in log) Console.WriteLine(line);
        throw;
    }
    finally
    {
        foreach (var lease in leases) await lease.DisposeAsync();
    }
});

await Run("Explicit custom endpoints still use the configured server", async () =>
{
    await using var outside = new FakeServer(0, 0, "custom-external");
    await using var warmup = await ViiperBridge.WarmupServerAsync($"127.0.0.1:{outside.ApiPort}", null, CancellationToken.None);
    Check(warmup.Description.Contains("custom-external"), "Custom endpoint ignored");
    Equal("custom-external", await Ping(outside.ApiPort));
});

await Run("Cancelled startup does not retain a managed server", async () =>
{
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try
    {
        await ViiperBridge.WarmupServerAsync("localhost:3242", Environment.ProcessPath, cancelled.Token);
        throw new Exception("Cancellation was ignored");
    }
    catch (OperationCanceledException) { }
});

await Run("Process exit stops only owned VIIPER children", async () =>
{
    await using var outside = TryCreateExternal();
    using var owner = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "owner-exit-test")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    })!;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try
    {
        var stdout = owner.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = owner.StandardError.ReadToEndAsync(timeout.Token);
        await owner.WaitForExitAsync(timeout.Token);
        Equal(0, owner.ExitCode);
        var output = await stdout;
        var pid = int.Parse(Regex.Match(output, @"managed-(\d+)").Groups[1].Value);
        await CheckStopped(pid);
        Check(string.IsNullOrEmpty(await stderr), "Owner helper reported an error");
        if (outside is not null) Equal("external", await Ping(3242));
    }
    finally
    {
        if (!owner.HasExited) { owner.Kill(entireProcessTree: true); await owner.WaitForExitAsync(); }
    }
});

Console.WriteLine($"Regression tests finished: {failures} failure(s).");
return failures == 0 ? 0 : 1;

async Task Run(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex}"); }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected '{expected}', got '{actual}'");
static int ParsePort(string address) => int.Parse(address[(address.LastIndexOf(':') + 1)..]);

static FakeServer? TryCreateExternal()
{
    try { return new FakeServer(3242, 3241, "external"); }
    catch (SocketException) { return null; }
}

static async Task<string> Ping(int port)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    using var client = new TcpClient();
    await client.ConnectAsync("127.0.0.1", port, timeout.Token);
    await client.GetStream().WriteAsync("ping\0"u8.ToArray(), timeout.Token);
    using var result = new MemoryStream();
    await client.GetStream().CopyToAsync(result, timeout.Token);
    using var json = JsonDocument.Parse(result.ToArray());
    return json.RootElement.GetProperty("Version").GetString()!;
}

static async Task CheckStopped(int pid)
{
    for (var attempt = 0; attempt < 20; attempt++)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return;
        }
        catch (ArgumentException) { return; }
        await Task.Delay(100);
    }
    throw new Exception($"Managed server process {pid} was not stopped");
}

sealed class FakeServer : IAsyncDisposable
{
    private readonly TcpListener _api;
    private readonly TcpListener _usb;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptTask;
    private readonly string _version;
    public ConcurrentQueue<string> Requests { get; } = new();
    public int ApiPort => ((IPEndPoint)_api.LocalEndpoint).Port;
    public Task Completion => _acceptTask;

    public FakeServer(int apiPort, int usbPort, string version)
    {
        _version = version;
        _api = new TcpListener(IPAddress.Loopback, apiPort);
        _usb = new TcpListener(IPAddress.Loopback, usbPort);
        try { _api.Start(); _usb.Start(); }
        catch { _api.Stop(); _usb.Stop(); _cts.Dispose(); throw; }
        _acceptTask = Accept();
    }

    private async Task Accept()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var client = await _api.AcceptTcpClientAsync(_cts.Token);
                using var request = new MemoryStream();
                var buffer = new byte[1];
                while (await client.GetStream().ReadAsync(buffer, _cts.Token) != 0 && buffer[0] != 0)
                    request.WriteByte(buffer[0]);
                var path = Encoding.UTF8.GetString(request.ToArray()).Split(' ')[0];
                Requests.Enqueue(path);
                var response = path switch
                {
                    "ping" => JsonSerializer.Serialize(new { Version = _version }),
                    "bus/list" => "{\"Buses\":[]}",
                    "bus/create" => "{\"BusId\":1}",
                    "bus/1/add" => "{\"Status\":409,\"Title\":\"Conflict\",\"Detail\":\"test-add-failure\"}",
                    _ => "{}"
                };
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(response), _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_cts.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _api.Stop();
        _usb.Stop();
        await _acceptTask;
        _cts.Dispose();
    }
}
