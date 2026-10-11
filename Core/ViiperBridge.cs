using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Switch2ProWirelessViiper.Core;

public sealed class ViiperBridge : IAsyncDisposable
{
    private const string AutoAttachLocalClientEnvironmentVariable = "VIIPER_API_AUTO_ATTACH_LOCAL_CLIENT";
    private static readonly SemaphoreSlim ServerLock = new(1, 1);
    private static readonly ConcurrentDictionary<int, Process> OwnedServerProcesses = new();
    private static ServerSession? _managedServer;
    private readonly ViiperApiClient _api;
    private readonly TcpClient _streamClient;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _readCts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _inputBuffer = new byte[ViiperNs2ProWire.InputSize];
    private readonly ServerLease _serverLease;
    private readonly bool _createdBus;
    private readonly uint _busId;
    private readonly string _devId;
    private readonly Task _readTask;
    private bool _disposed;

    public event EventHandler<byte[]>? HidOutputReceived;

    public string Description { get; }
    public bool VirtualDeviceReady { get; }
    public string VirtualDeviceInstanceId { get; }

    static ViiperBridge()
    {
        // WinUI window-close handlers cannot keep the application alive while awaiting cleanup.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var process in OwnedServerProcesses.Values) TryStopOwnedServer(process);
        };
    }

    public static async Task<ViiperServerWarmup> WarmupServerAsync(
        string apiAddress,
        string? viiperExePath,
        CancellationToken cancellationToken,
        Action<string>? trace = null)
    {
        var lease = await AcquireServerAsync(apiAddress, viiperExePath, cancellationToken, trace).ConfigureAwait(false);
        return new ViiperServerWarmup(
            lease.DisposeAsync,
            $"VIIPER server {lease.Session.Endpoint} v{lease.Session.Version} ready");
    }

    private ViiperBridge(
        ViiperApiClient api,
        TcpClient streamClient,
        ServerLease serverLease,
        bool createdBus,
        uint busId,
        string devId,
        string serverVersion,
        ViiperEndpoint endpoint,
        string virtualDeviceInstanceId)
    {
        _api = api;
        _streamClient = streamClient;
        _stream = streamClient.GetStream();
        _serverLease = serverLease;
        _createdBus = createdBus;
        _busId = busId;
        _devId = devId;
        VirtualDeviceReady = true;
        VirtualDeviceInstanceId = virtualDeviceInstanceId;
        Description = $"VIIPER ns2pro {endpoint} v{serverVersion} bus={busId} dev={devId}, virtual USB ready";
        _readTask = Task.Run(() => ReadLoopAsync(_readCts.Token));
    }

    public static async Task<ViiperBridge> ConnectAsync(
        string apiAddress,
        string? viiperExePath,
        CancellationToken cancellationToken,
        Action<string>? trace = null)
    {
        var lease = await AcquireServerAsync(apiAddress, viiperExePath, cancellationToken, trace).ConfigureAwait(false);
        var session = lease.Session;
        var endpoint = session.Endpoint;
        var api = new ViiperApiClient(endpoint);
        uint busId = 0;
        var createdBus = false;
        DeviceResponse? device = null;
        TcpClient? stream = null;
        try
        {
            (busId, createdBus) = await FindOrCreateBusAsync(api, cancellationToken).ConfigureAwait(false);
            try
            {
                device = await api.AddNs2ProAsync(busId, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (LooksLikeViiperAutoAttachFailure(ex))
            {
                throw new InvalidOperationException(
                    DescribeAutoAttachFailure(ex),
                    ex);
            }
            trace?.Invoke($"VIIPER created ns2pro device {device.BusId}-{device.DevId}.");
            stream = await api.OpenStreamAsync(device.BusId, device.DevId, cancellationToken).ConfigureAwait(false);
            trace?.Invoke($"VIIPER ns2pro stream opened for {device.BusId}-{device.DevId}.");
            var virtualDeviceInstanceId = await UsbipVirtualController.EnsureAttachedAsync(
                    endpoint.Host,
                    device.BusId,
                    device.DevId,
                    trace,
                    cancellationToken,
                    session.UsbipPort,
                    allowExistingDevice: session.Process is null)
                .ConfigureAwait(false);
            return new ViiperBridge(
                api,
                stream,
                lease,
                createdBus,
                device.BusId,
                device.DevId,
                session.Version,
                endpoint,
                virtualDeviceInstanceId);
        }
        catch
        {
            stream?.Dispose();
            if (device is not null)
            {
                await TryCleanupDeviceAsync(api, device.BusId, device.DevId).ConfigureAwait(false);
            }

            if (createdBus)
            {
                await TryCleanupBusAsync(api, busId).ConfigureAwait(false);
            }

            await lease.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    public async Task SubmitAsync(Switch2State state, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ViiperNs2ProWire.WriteInput(_inputBuffer, state);
            await _stream.WriteAsync(_inputBuffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Submit(Switch2State state, CancellationToken cancellationToken)
    {
        _writeLock.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ViiperNs2ProWire.WriteInput(_inputBuffer, state);
            _stream.Write(_inputBuffer, 0, _inputBuffer.Length);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _readCts.Cancel();
        _streamClient.Close();
        await _readTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stream.Dispose();
        _streamClient.Dispose();
        _writeLock.Dispose();
        _readCts.Dispose();

        await TryCleanupDeviceAsync(_api, _busId, _devId).ConfigureAwait(false);
        if (_createdBus)
        {
            await TryCleanupBusAsync(_api, _busId).ConfigureAwait(false);
        }

        await _serverLease.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ViiperNs2ProWire.OutputSize];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReadExactlyAsync(_stream, buffer, cancellationToken).ConfigureAwait(false);
                if ((buffer[32] & ViiperNs2ProWire.OutputFlagRumble) != 0)
                {
                    HidOutputReceived?.Invoke(this, ViiperNs2ProWire.BuildHidOutputFromFeedback(buffer));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("VIIPER device stream closed.");
            }

            offset += read;
        }
    }

    private static async Task<PingResponse?> TryPingAsync(ViiperApiClient api, CancellationToken cancellationToken, Action<string>? onFailure = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            return await api.PingAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            onFailure?.Invoke(ex.Message);
            return null;
        }
    }

    private static async Task<PingResponse?> WaitForPingAsync(
        ViiperApiClient api,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Process? process = null,
        Action<string>? trace = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        string? lastFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process is { HasExited: true })
            {
                throw new InvalidOperationException($"VIIPER exited during startup with code {process.ExitCode}. Check the server log and listening ports.");
            }
            var ping = await TryPingAsync(api, cancellationToken, failure =>
            {
                if (failure == lastFailure) return;
                lastFailure = failure;
                trace?.Invoke("VIIPER startup ping failed: " + failure);
            }).ConfigureAwait(false);
            if (ping is not null)
            {
                return ping;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<(uint BusId, bool Created)> FindOrCreateBusAsync(
        ViiperApiClient api,
        CancellationToken cancellationToken)
    {
        var buses = await api.BusListAsync(cancellationToken).ConfigureAwait(false);
        if (buses.Buses.Length > 0)
        {
            return (buses.Buses.Min(), false);
        }

        var created = await api.BusCreateAsync(0, cancellationToken).ConfigureAwait(false);
        return (created.BusId, true);
    }

    private static async Task<ServerLease> AcquireServerAsync(
        string apiAddress,
        string? viiperExePath,
        CancellationToken cancellationToken,
        Action<string>? trace)
    {
        var requested = ViiperEndpoint.Parse(string.IsNullOrWhiteSpace(apiAddress) ? "localhost:3242" : apiAddress);
        var managedLocal = requested.IsDefaultLocal;
        await ServerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (managedLocal && _managedServer is { IsRunning: true } existing &&
                string.Equals(existing.RequestedExePath, viiperExePath, StringComparison.OrdinalIgnoreCase))
            {
                var existingApi = new ViiperApiClient(existing.Endpoint);
                if (await TryPingAsync(existingApi, cancellationToken).ConfigureAwait(false) is not null)
                {
                    existing.References++;
                    trace?.Invoke($"Using this app's managed VIIPER server: pid={existing.Process!.Id}, API={existing.Endpoint}, USBIP=127.0.0.1:{existing.UsbipPort}.");
                    return new ServerLease(existing);
                }

                existing.Stop();
                _managedServer = null;
            }

            var endpoint = requested;
            var usbipPort = 3241;
            PingResponse? ping = null;
            // A successful ping does not prove that an external server has auto-attach disabled.
            if (!managedLocal)
            {
                ping = await TryPingAsync(new ViiperApiClient(endpoint), cancellationToken).ConfigureAwait(false);
            }

            Process? process = null;
            try
            {
                if (ping is null)
                {
                    var exe = ResolveViiperExe(viiperExePath) ?? throw new FileNotFoundException(
                        "viiper.exe was not found. Put viiper.exe beside this app or browse to it.");
                    if (managedLocal)
                    {
                        (endpoint, usbipPort) = SelectManagedPorts();
                        if (endpoint.Port != 3242)
                        {
                            trace?.Invoke($"Default VIIPER ports are occupied; leaving the existing service untouched. Managed API={endpoint}, USBIP=127.0.0.1:{usbipPort}.");
                        }
                    }

                    process = StartViiperServer(exe, endpoint, usbipPort, trace);
                    ping = await WaitForPingAsync(new ViiperApiClient(endpoint), TimeSpan.FromSeconds(8), cancellationToken, process, trace).ConfigureAwait(false);
                }

                if (ping is null)
                {
                    throw new InvalidOperationException($"VIIPER API is not reachable at {endpoint}.");
                }

                if (process is null)
                {
                    trace?.Invoke("Using explicitly configured external VIIPER server; its auto-attach setting is not controlled by this app.");
                }

                var session = new ServerSession(process, endpoint, usbipPort, ping.Version ?? "unknown", viiperExePath);
                if (managedLocal)
                {
                    _managedServer = session;
                }

                return new ServerLease(session);
            }
            catch
            {
                if (process is not null)
                {
                    TryStopOwnedServer(process);
                }

                throw;
            }
        }
        finally
        {
            ServerLock.Release();
        }
    }

    private static (ViiperEndpoint Endpoint, int UsbipPort) SelectManagedPorts()
    {
        for (var apiPort = 3242; apiPort <= 3272; apiPort += 2)
        {
            var usbipPort = apiPort - 1;
            var apiListener = new TcpListener(IPAddress.Loopback, apiPort);
            var usbipListener = new TcpListener(IPAddress.Loopback, usbipPort);
            apiListener.ExclusiveAddressUse = true;
            usbipListener.ExclusiveAddressUse = true;
            try
            {
                apiListener.Start();
                usbipListener.Start();
                return (new ViiperEndpoint("127.0.0.1", apiPort), usbipPort);
            }
            catch (SocketException)
            {
            }
            finally
            {
                apiListener.Stop();
                usbipListener.Stop();
            }
        }

        throw new InvalidOperationException("No free local VIIPER API/USBIP port pair was found in 3241-3272. No existing server was stopped.");
    }

    private static string? ResolveViiperExe(string? explicitPath)
    {
        var candidates = new List<string?>();
        candidates.Add(explicitPath);
        candidates.Add(Environment.GetEnvironmentVariable("VIIPER_EXE"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "viiper.exe"));

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "viiper.exe"));
        }

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .FirstOrDefault(File.Exists);
    }

    private static Process StartViiperServer(
        string exePath,
        ViiperEndpoint endpoint,
        int usbipPort,
        Action<string>? trace)
    {
        var info = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"server --api.addr={endpoint.ListenAddress} --usb.addr=127.0.0.1:{usbipPort} --api.auto-attach-local-client=false --update-notify=none",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment[AutoAttachLocalClientEnvironmentVariable] = "false";
        trace?.Invoke($"Starting managed VIIPER with auto-attach and update checks disabled: exe='{exePath}', API={endpoint}, USBIP=127.0.0.1:{usbipPort}. This app will attach USBIP manually.");
        var process = new Process
        {
            StartInfo = info,
            EnableRaisingEvents = true,
        };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                trace?.Invoke("VIIPER: " + args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                trace?.Invoke("VIIPER error: " + args.Data);
            }
        };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Failed to start VIIPER server: {exePath}");
            OwnedServerProcesses.TryAdd(process.Id, process);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            trace?.Invoke($"Managed VIIPER started: pid={process.Id}.");
            return process;
        }
        catch
        {
            TryStopOwnedServer(process);
            throw;
        }
    }

    internal static string DescribeAutoAttachFailure(Exception exception)
    {
        var details = exception.Message;
        var message = exception.ToString();
        var reason = message.Contains("executable file not found", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("not found in %PATH%", StringComparison.OrdinalIgnoreCase)
            ? "The external VIIPER server cannot find usbip.exe in its process PATH. This is not evidence of a missing driver or an ABI mismatch."
            : UsbipVirtualController.ClassifyFailure(message) switch
            {
                UsbipFailureKind.MultipleVhciInterfaces => "The USBIP driver exposes multiple VHCI host-controller interfaces. This is a controller/driver-instance conflict, not a missing or incompatible usbip.exe.",
                UsbipFailureKind.AbiMismatch => "The USBIP client reported an ABI mismatch with the installed driver.",
                _ => "The external VIIPER server's auto-attach operation failed; the exact error is shown below."
            };
        return $"VIIPER failed while adding the ns2pro virtual device. {reason} " +
               $"For an explicitly configured external server, start it with --api.auto-attach-local-client=false and let this app attach USBIP. Details: {details}";
    }


    private static bool LooksLikeViiperAutoAttachFailure(Exception exception)
    {
        var message = exception.ToString();
        return message.Contains("auto-attach", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("DeviceIoControl", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("IOControl", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("usbip", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task TryCleanupDeviceAsync(ViiperApiClient api, uint busId, string devId)
    {
        try
        {
            await api.DeviceRemoveAsync(busId, devId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task TryCleanupBusAsync(ViiperApiClient api, uint busId)
    {
        try
        {
            await api.BusRemoveAsync(busId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void TryStopOwnedServer(Process process)
    {
        try
        {
            OwnedServerProcesses.TryRemove(process.Id, out _);
            process.CloseMainWindow();
            if (!process.WaitForExit(1000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private sealed class ServerSession(
        Process? process, ViiperEndpoint endpoint, int usbipPort, string version, string? requestedExePath)
    {
        private bool _stopped;
        public Process? Process { get; } = process;
        public ViiperEndpoint Endpoint { get; } = endpoint;
        public int UsbipPort { get; } = usbipPort;
        public string Version { get; } = version;
        public string? RequestedExePath { get; } = requestedExePath;
        public int References { get; set; } = 1;
        public bool IsRunning => !_stopped && Process is { HasExited: false };

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            if (Process is not null) TryStopOwnedServer(Process);
        }
    }

    private sealed class ServerLease(ServerSession session) : IAsyncDisposable
    {
        private int _disposed;
        public ServerSession Session { get; } = session;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await ServerLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (--Session.References == 0)
                {
                    Session.Stop();
                    if (ReferenceEquals(_managedServer, Session)) _managedServer = null;
                }
            }
            finally
            {
                ServerLock.Release();
            }
        }
    }

    private sealed class ViiperApiClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
        private readonly ViiperEndpoint _endpoint;

        public ViiperApiClient(ViiperEndpoint endpoint)
        {
            _endpoint = endpoint;
        }

        public async Task<PingResponse> PingAsync(CancellationToken cancellationToken) =>
            await RequestJsonAsync<PingResponse>("ping", null, cancellationToken).ConfigureAwait(false);

        public async Task<BusListResponse> BusListAsync(CancellationToken cancellationToken) =>
            await RequestJsonAsync<BusListResponse>("bus/list", null, cancellationToken).ConfigureAwait(false);

        public async Task<BusCreateResponse> BusCreateAsync(uint busId, CancellationToken cancellationToken) =>
            await RequestJsonAsync<BusCreateResponse>("bus/create", busId.ToString(), cancellationToken).ConfigureAwait(false);

        public async Task BusRemoveAsync(uint busId, CancellationToken cancellationToken) =>
            await RequestAsync("bus/remove", busId.ToString(), cancellationToken).ConfigureAwait(false);

        public async Task<DeviceResponse> AddNs2ProAsync(uint busId, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "ns2pro",
            });
            return await RequestJsonAsync<DeviceResponse>($"bus/{busId}/add", payload, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task DeviceRemoveAsync(uint busId, string devId, CancellationToken cancellationToken) =>
            await RequestAsync($"bus/{busId}/remove", devId, cancellationToken).ConfigureAwait(false);

        public async Task<TcpClient> OpenStreamAsync(uint busId, string devId, CancellationToken cancellationToken)
        {
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(_endpoint.Host, _endpoint.Port, cancellationToken).ConfigureAwait(false);
            var payload = Encoding.UTF8.GetBytes($"bus/{busId}/{devId}\0");
            await client.GetStream().WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            return client;
        }

        private async Task<T> RequestJsonAsync<T>(string path, string? payload, CancellationToken cancellationToken)
        {
            var response = await RequestAsync(path, payload, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response))
            {
                throw new InvalidOperationException($"VIIPER {path} returned an empty response.");
            }

            var error = TryParseApiError(response);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            return JsonSerializer.Deserialize<T>(response, JsonOptions)
                ?? throw new InvalidOperationException($"VIIPER {path} returned invalid JSON: {response}");
        }

        private async Task<string> RequestAsync(string path, string? payload, CancellationToken cancellationToken)
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(_endpoint.Host, _endpoint.Port, cancellationToken).ConfigureAwait(false);
            var request = string.IsNullOrEmpty(payload) ? $"{path}\0" : $"{path} {payload}\0";
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(request), cancellationToken).ConfigureAwait(false);

            using var ms = new MemoryStream();
            await client.GetStream().CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\n');
        }

        private static string? TryParseApiError(string response)
        {
            try
            {
                var error = JsonSerializer.Deserialize<ApiErrorResponse>(response, JsonOptions);
                if (error is not null && (!string.IsNullOrWhiteSpace(error.Title) || error.Status != 0))
                {
                    return error.Status == 0
                        ? $"{error.Title}: {error.Detail}"
                        : $"{error.Status} {error.Title}: {error.Detail}";
                }
            }
            catch
            {
            }

            return null;
        }
    }

    private sealed record ViiperEndpoint(string Host, int Port)
    {
        public static ViiperEndpoint Parse(string address)
        {
            var value = address.Trim();
            if (value.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            {
                value = value[6..];
            }

            var split = value.LastIndexOf(':');
            if (split < 0)
            {
                return new ViiperEndpoint(NormalizeHost(value), 3242);
            }

            var host = value[..split];
            var portText = value[(split + 1)..];
            if (!int.TryParse(portText, out var port) || port <= 0 || port > 65535)
            {
                throw new ArgumentException($"Invalid VIIPER API address: {address}");
            }

            return new ViiperEndpoint(NormalizeHost(host), port);
        }

        public string ListenAddress => $"{(IsLocalHost(Host) ? "127.0.0.1" : Host)}:{Port}";
        public override string ToString() => $"{Host}:{Port}";

        private static string NormalizeHost(string host)
        {
            host = host.Trim().Trim('[', ']');
            return string.IsNullOrWhiteSpace(host) || host is "*" or "0.0.0.0"
                ? "127.0.0.1"
                : host;
        }

        public bool IsDefaultLocal => Port == 3242 && IsLocalHost(Host);

        private static bool IsLocalHost(string host) =>
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PingResponse
    {
        public string? Version { get; set; }
    }

    private sealed class BusListResponse
    {
        public uint[] Buses { get; set; } = [];
    }

    private sealed class BusCreateResponse
    {
        public uint BusId { get; set; }
    }

    private sealed class DeviceResponse
    {
        public uint BusId { get; set; }
        public string DevId { get; set; } = string.Empty;
    }

    private sealed class ApiErrorResponse
    {
        public int Status { get; set; }
        public string? Title { get; set; }
        public string? Detail { get; set; }
    }
}

public sealed class ViiperServerWarmup : IAsyncDisposable
{
    private readonly Func<ValueTask> _releaseServer;
    private int _disposed;

    internal ViiperServerWarmup(Func<ValueTask> releaseServer, string description)
    {
        _releaseServer = releaseServer;
        Description = description;
    }

    public string Description { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return _releaseServer();
    }
}
