using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Switch2ProWirelessViiper.Core;

public sealed class ViiperBridge : IAsyncDisposable
{
    private readonly ViiperApiClient _api;
    private readonly TcpClient _streamClient;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _readCts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _inputBuffer = new byte[ViiperNs2ProWire.InputSize];
    private readonly Process? _ownedServer;
    private readonly bool _createdBus;
    private readonly uint _busId;
    private readonly string _devId;
    private readonly Task _readTask;
    private bool _disposed;

    public event EventHandler<byte[]>? HidOutputReceived;

    public string Description { get; }
    public bool VirtualDeviceReady { get; }
    public string VirtualDeviceInstanceId { get; }

    public static async Task<ViiperServerWarmup> WarmupServerAsync(
        string apiAddress,
        string? viiperExePath,
        CancellationToken cancellationToken,
        Action<string>? trace = null)
    {
        var endpoint = ViiperEndpoint.Parse(string.IsNullOrWhiteSpace(apiAddress) ? "localhost:3242" : apiAddress);
        var api = new ViiperApiClient(endpoint);
        Process? serverProcess = null;

        try
        {
            var ping = await TryPingAsync(api, cancellationToken).ConfigureAwait(false);
            if (ping is null)
            {
                var exe = ResolveViiperExe(viiperExePath);
                if (exe is null)
                {
                    throw new FileNotFoundException(
                        "VIIPER is not running and viiper.exe was not found. Put viiper.exe beside this app or browse to it.");
                }

                serverProcess = StartViiperServer(exe, endpoint, trace);
                ping = await WaitForPingAsync(api, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            }

            if (ping is null)
            {
                throw new InvalidOperationException($"VIIPER API is not reachable at {endpoint}.");
            }

            return new ViiperServerWarmup(
                serverProcess,
                $"VIIPER server {endpoint} v{ping.Version ?? "unknown"} ready");
        }
        catch
        {
            if (serverProcess is not null && !serverProcess.HasExited)
            {
                TryStopOwnedServer(serverProcess);
            }

            throw;
        }
    }

    private ViiperBridge(
        ViiperApiClient api,
        TcpClient streamClient,
        Process? ownedServer,
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
        _ownedServer = ownedServer;
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
        var endpoint = ViiperEndpoint.Parse(string.IsNullOrWhiteSpace(apiAddress) ? "localhost:3242" : apiAddress);
        var api = new ViiperApiClient(endpoint);
        Process? serverProcess = null;

        var ping = await TryPingAsync(api, cancellationToken).ConfigureAwait(false);
        if (ping is null)
        {
            var exe = ResolveViiperExe(viiperExePath);
            if (exe is null)
            {
                throw new FileNotFoundException(
                    "VIIPER is not running and viiper.exe was not found. Put viiper.exe beside this app or browse to it.");
            }

            serverProcess = StartViiperServer(exe, endpoint, trace);
            ping = await WaitForPingAsync(api, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        }

        if (ping is null)
        {
            throw new InvalidOperationException($"VIIPER API is not reachable at {endpoint}.");
        }

        var (busId, createdBus) = await FindOrCreateBusAsync(api, cancellationToken).ConfigureAwait(false);
        DeviceResponse? device = null;
        TcpClient? stream = null;
        try
        {
            device = await api.AddNs2ProAsync(busId, cancellationToken).ConfigureAwait(false);
            trace?.Invoke($"VIIPER created ns2pro device {device.BusId}-{device.DevId}.");
            stream = await api.OpenStreamAsync(device.BusId, device.DevId, cancellationToken).ConfigureAwait(false);
            trace?.Invoke($"VIIPER ns2pro stream opened for {device.BusId}-{device.DevId}.");
            var virtualDeviceInstanceId = await UsbipVirtualController.EnsureAttachedAsync(
                    endpoint.Host,
                    device.BusId,
                    device.DevId,
                    trace,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ViiperBridge(
                api,
                stream,
                serverProcess,
                createdBus,
                device.BusId,
                device.DevId,
                ping.Version ?? "unknown",
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

            if (serverProcess is not null && !serverProcess.HasExited)
            {
                TryStopOwnedServer(serverProcess);
            }

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

        if (_ownedServer is not null && !_ownedServer.HasExited)
        {
            TryStopOwnedServer(_ownedServer);
        }
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

    private static async Task<PingResponse?> TryPingAsync(ViiperApiClient api, CancellationToken cancellationToken)
    {
        try
        {
            return await api.PingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<PingResponse?> WaitForPingAsync(
        ViiperApiClient api,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ping = await TryPingAsync(api, cancellationToken).ConfigureAwait(false);
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
        Action<string>? trace)
    {
        var info = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"server --api.addr={endpoint.ListenAddress}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
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
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start VIIPER server: {exePath}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
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
            process.CloseMainWindow();
            if (!process.WaitForExit(1000))
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
        }
        catch
        {
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
    private readonly Process? _ownedServer;
    private bool _disposed;

    internal ViiperServerWarmup(Process? ownedServer, string description)
    {
        _ownedServer = ownedServer;
        Description = description;
    }

    public string Description { get; }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        if (_ownedServer is not null && !_ownedServer.HasExited)
        {
            try
            {
                _ownedServer.CloseMainWindow();
                if (!_ownedServer.WaitForExit(1000))
                {
                    _ownedServer.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }

        _ownedServer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
