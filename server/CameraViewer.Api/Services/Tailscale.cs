using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CameraViewer.Api.Services;

public sealed record TailnetPeer(string Name, string? DnsName, string[] Addresses, bool Online, string? Os, string[] Routes);

/// <summary>
/// State is "Unavailable" (helper binary missing), "Disabled", "Starting", "NeedsLogin", "NeedsMachineAuth",
/// "Running", "Stopped" or "Error"; the middle ones come straight from Tailscale's backend.
/// </summary>
public sealed record TailscaleStatus(
    bool Available, bool Enabled, string Hostname, string State, string? AuthUrl, string? DnsName, string[] Addresses,
    string? Tailnet, bool? AcceptRoutes, List<TailnetPeer> Peers, string[] Health, string? Error);

public sealed class TailscaleSettings
{
    public bool Enabled { get; set; }
    public string Hostname { get; set; } = "camera-viewer";
}

/// <summary>The helper's loopback SOCKS5 proxy onto the tailnet (user "tsnet").</summary>
public sealed record TailnetProxy(string Host, int Port, string Password);

/// <summary>
/// Runs Camera Viewer's own Tailscale node: a bundled helper (tailscale/tsproxy, built on Tailscale's tsnet
/// library) that joins the tailnet in userspace, so no system Tailscale install, admin rights or network
/// adapter are needed. Devices marked "use Tailscale" are reached through the helper's SOCKS5 proxy: ISAPI
/// requests directly, and RTSP via a loopback port forwarder because ffmpeg can't use a SOCKS proxy.
/// </summary>
public sealed class TailscaleService : BackgroundService
{
    private const int MaxLogLines = 40;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly AppPaths _paths;
    private readonly ILogger<TailscaleService> _log;
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, RtspForwarder> _forwarders = new();
    private readonly ConcurrentQueue<string> _helperLog = new();

    private Helper? _helper;
    private string? _pendingAuthKey;
    private string? _lastError;
    private int _failures;
    private DateTime _nextAttemptUtc;
    private DateTime _lastLoginRequestUtc;

    public TailscaleService(AppPaths paths, IConfiguration config, ILogger<TailscaleService> log)
    {
        _paths = paths;
        _log = log;
        _settingsPath = Path.Combine(paths.DataDir, "tailscale.json");
        Settings = LoadSettings();
        BinaryPath = ResolveBinary(config["Tailscale:HelperPath"]);
    }

    public TailscaleSettings Settings { get; private set; }

    /// <summary>Null when the helper wasn't built for this platform (see tailscale/build.ps1).</summary>
    public string? BinaryPath { get; }

    // Not named "tailscale": on Windows tsnet then tries to take ownership of the folder, which fails where the user
    // lacks that right (e.g. a data drive from another Windows install). The app secures the folder itself instead.
    private string StateDir => Path.Combine(_paths.DataDir, "tailscale-node");

    // ---------- public API ----------

    public async Task UpdateSettingsAsync(bool enabled, string hostname, CancellationToken ct)
    {
        var renamed = hostname != Settings.Hostname;
        Settings = new TailscaleSettings { Enabled = enabled, Hostname = hostname };
        await File.WriteAllTextAsync(_settingsPath, JsonSerializer.Serialize(Settings, Json), ct);

        // The machine name can change live; enabling/disabling is handled by the supervisor loop.
        if (renamed && _helper is { } h && !h.HasExited)
            await LocalApiAsync(h, HttpMethod.Patch, "prefs", new JsonObject { ["HostnameSet"] = true, ["Hostname"] = hostname }, ct);
        Wake();
    }

    /// <summary>Starts an interactive login (the status then carries an AuthUrl), or logs in with an auth key.</summary>
    public async Task LoginAsync(string? authKey, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(authKey))
        {
            // tsnet only reads the key at start-up, so restart the helper with it (passed via env, never stored).
            _pendingAuthKey = authKey;
            await RestartAsync();
            return;
        }
        var helper = RequireHelper();
        _lastLoginRequestUtc = DateTime.UtcNow;
        await LocalApiAsync(helper, HttpMethod.Post, "login-interactive", null, ct);
    }

    public async Task LogoutAsync(CancellationToken ct) =>
        await LocalApiAsync(RequireHelper(), HttpMethod.Post, "logout", null, ct);

    public async Task SetAcceptRoutesAsync(bool accept, CancellationToken ct) =>
        await LocalApiAsync(RequireHelper(), HttpMethod.Patch, "prefs", new JsonObject { ["RouteAllSet"] = true, ["RouteAll"] = accept }, ct);

    public async Task<TailscaleStatus> GetStatusAsync(CancellationToken ct)
    {
        TailscaleStatus Simple(string state, string? error = null) =>
            new(BinaryPath is not null, Settings.Enabled, Settings.Hostname, state, null, null, [], null, null, [], [], error);

        if (BinaryPath is null) return Simple("Unavailable", "The Tailscale helper isn't installed for this platform. Build it with tailscale/build.ps1 (or build.sh).");
        if (!Settings.Enabled) return Simple("Disabled");
        if (_helper is not { HasExited: false } helper) return Simple(_lastError is null ? "Starting" : "Error", _lastError);

        JsonNode? status, prefs;
        try
        {
            status = await LocalApiAsync(helper, HttpMethod.Get, "status", null, ct);
            prefs = await LocalApiAsync(helper, HttpMethod.Get, "prefs", null, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or AppException)
        {
            return Simple("Starting", ex.Message);
        }

        var state = status?["BackendState"]?.GetValue<string>() ?? "Starting";
        var authUrl = NullIfEmpty(status?["AuthURL"]?.GetValue<string>());
        // A fresh node may sit in NeedsLogin without a URL; ask for one (at most once a minute).
        if (state == "NeedsLogin" && authUrl is null && DateTime.UtcNow - _lastLoginRequestUtc > TimeSpan.FromMinutes(1))
        {
            _lastLoginRequestUtc = DateTime.UtcNow;
            try { await LocalApiAsync(helper, HttpMethod.Post, "login-interactive", null, ct); }
            catch (HttpRequestException) { }
        }

        var self = status?["Self"];
        var peers = (status?["Peer"] as JsonObject)?.Select(p => p.Value).Where(p => p is not null).Select(p => new TailnetPeer(
                p!["HostName"]?.GetValue<string>() ?? "",
                TrimDot(p["DNSName"]?.GetValue<string>()),
                Strings(p["TailscaleIPs"]),
                p["Online"]?.GetValue<bool>() ?? false,
                NullIfEmpty(p["OS"]?.GetValue<string>()),
                Strings(p["PrimaryRoutes"])))
            .OrderByDescending(p => p.Routes.Length > 0).ThenBy(p => p.Name).ToList() ?? [];

        return new TailscaleStatus(true, true, Settings.Hostname, state, authUrl,
            TrimDot(self?["DNSName"]?.GetValue<string>()), Strings(self?["TailscaleIPs"]),
            NullIfEmpty(status?["CurrentTailnet"]?["Name"]?.GetValue<string>()),
            prefs?["RouteAll"]?.GetValue<bool>(), peers, Strings(status?["Health"]), _lastError);
    }

    /// <summary>Opens a TCP connection to <paramref name="host"/>:<paramref name="port"/> through the tailnet.</summary>
    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var socket = await Socks5.ConnectAsync(RequireProxy(), host, port, ct);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>
    /// The RTSP address ffmpeg should use for a device: unchanged for local devices, or a loopback port that
    /// forwards through the tailnet for Tailscale ones.
    /// </summary>
    public DeviceConnection ForRtsp(DeviceConnection c)
    {
        if (!c.ViaTailscale) return c;
        RequireProxy(); // fail early with a clear message if Tailscale isn't up
        var forwarder = _forwarders.GetOrAdd($"{c.Host}:{c.RtspPort}", _ => new RtspForwarder(c.Host, c.RtspPort, this, _log));
        return c with { Host = "127.0.0.1", RtspPort = forwarder.Port };
    }

    // ---------- supervisor ----------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (BinaryPath is null)
            _log.LogInformation("Tailscale helper not found; devices can't be reached over Tailscale until it is built (tailscale/build.ps1)");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _gate.WaitAsync(stoppingToken);
                try
                {
                    if (_helper is { HasExited: true } dead)
                    {
                        _lastError = $"The Tailscale helper stopped: {dead.LastLines(3)}";
                        _log.LogWarning("Tailscale helper exited: {Log}", dead.LastLines(5));
                        dead.Dispose();
                        _helper = null;
                        ScheduleRetry();
                    }
                    if (Settings.Enabled && BinaryPath is not null && _helper is null && DateTime.UtcNow >= _nextAttemptUtc)
                        await StartHelperAsync(stoppingToken);
                    if (!Settings.Enabled && _helper is not null)
                        StopHelper();
                }
                finally
                {
                    _gate.Release();
                }
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            StopHelper();
            foreach (var f in _forwarders.Values) f.Dispose();
        }
    }

    private async Task StartHelperAsync(CancellationToken ct)
    {
        AppPaths.EnsureOwnerOnlyDirectory(StateDir);
        var psi = new ProcessStartInfo(BinaryPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-dir");
        psi.ArgumentList.Add(StateDir);
        psi.ArgumentList.Add("-hostname");
        psi.ArgumentList.Add(Settings.Hostname);
        if (_pendingAuthKey is { } key) psi.Environment["TS_AUTHKEY"] = key;
        _pendingAuthKey = null;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (sender, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            _helperLog.Enqueue(e.Data);
            while (_helperLog.Count > MaxLogLines) _helperLog.TryDequeue(out _);
            _log.LogDebug("tsproxy: {Line}", e.Data);
        };
        try
        {
            process.Start();
            ChildProcessJob.Attach(process);
            process.BeginErrorReadLine();

            // The helper announces its loopback endpoint and credentials as one JSON line.
            var line = await process.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct)
                ?? throw new InvalidOperationException("the helper exited during start-up");
            var hello = JsonNode.Parse(line)!;
            var addr = hello["addr"]!.GetValue<string>();
            var helper = new Helper(process, addr, hello["proxyCred"]!.GetValue<string>(), hello["localApiCred"]!.GetValue<string>(), _helperLog);
            _helper = helper;
            _lastError = null;
            _failures = 0;
            _log.LogInformation("Tailscale helper started as '{Host}' (local endpoint {Addr})", Settings.Hostname, addr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
            _lastError = $"Couldn't start the Tailscale helper: {ex.Message}. {string.Join(" ", _helperLog.TakeLast(2))}";
            _log.LogWarning("{Error}", _lastError);
            ScheduleRetry();
        }
    }

    private void StopHelper()
    {
        if (_helper is null) return;
        _helper.Dispose();
        _helper = null;
        _log.LogInformation("Tailscale helper stopped");
    }

    private async Task RestartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            StopHelper();
            _nextAttemptUtc = DateTime.MinValue;
        }
        finally
        {
            _gate.Release();
        }
        Wake();
    }

    private void ScheduleRetry()
    {
        _failures++;
        _nextAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(60, 5 * _failures));
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private Helper RequireHelper() =>
        _helper is { HasExited: false } h ? h : throw new AppException("Tailscale isn't running. Turn it on first.", StatusCodes.Status409Conflict);

    private TailnetProxy RequireProxy()
    {
        if (_helper is not { HasExited: false } h)
            throw new AppException(Settings.Enabled
                ? "Tailscale is still starting or has stopped; check the Tailscale page."
                : "This device is set to connect through Tailscale, but Tailscale is turned off. Turn it on on the Tailscale page.",
                StatusCodes.Status503ServiceUnavailable);
        var colon = h.Addr.LastIndexOf(':');
        return new TailnetProxy(h.Addr[..colon], int.Parse(h.Addr[(colon + 1)..]), h.ProxyCred);
    }

    private static async Task<JsonNode?> LocalApiAsync(Helper helper, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"/localapi/v0/{path}");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await helper.LocalApi.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AppException($"Tailscale: {text.Trim()}", StatusCodes.Status502BadGateway);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    // ---------- helpers ----------

    private TailscaleSettings LoadSettings()
    {
        try
        {
            return File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<TailscaleSettings>(File.ReadAllText(_settingsPath), Json) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static string? ResolveBinary(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : null;
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (os is null || arch is null) return null;
        var path = Path.Combine(AppContext.BaseDirectory, "tailscale", $"{os}-{arch}", OperatingSystem.IsWindows() ? "tsproxy.exe" : "tsproxy");
        return File.Exists(path) ? path : null;
    }

    private static string[] Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(x => x?.GetValue<string>()).OfType<string>().ToArray() : [];

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? TrimDot(string? dns) => NullIfEmpty(dns)?.TrimEnd('.');

    /// <summary>A running helper process and its loopback endpoint.</summary>
    private sealed class Helper(Process process, string addr, string proxyCred, string localApiCred, ConcurrentQueue<string> log) : IDisposable
    {
        public string Addr { get; } = addr;
        public string ProxyCred { get; } = proxyCred;

        public HttpClient LocalApi { get; } = CreateLocalApi(addr, localApiCred);

        private static HttpClient CreateLocalApi(string addr, string credential)
        {
            var client = new HttpClient { BaseAddress = new Uri($"http://{addr}"), Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Add("Sec-Tailscale", "localapi");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + credential)));
            return client;
        }

        public bool HasExited
        {
            get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } }
        }

        public string LastLines(int n) => string.Join(" | ", log.TakeLast(n));

        public void Dispose()
        {
            LocalApi.Dispose();
            try
            {
                // Closing stdin tells the helper to shut down cleanly (its node state stays on disk).
                process.StandardInput.Close();
                if (!process.WaitForExit(5000)) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
            process.Dispose();
        }
    }

    /// <summary>Listens on a loopback port and forwards each connection to host:port through the tailnet.</summary>
    private sealed class RtspForwarder : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();

        public RtspForwarder(string host, int port, TailscaleService tailscale, ILogger log)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync(host, port, tailscale, log);
        }

        public int Port { get; }

        private async Task AcceptLoopAsync(string host, int port, TailscaleService tailscale, ILogger log)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = PipeAsync(client, host, port, tailscale, log);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        private async Task PipeAsync(TcpClient client, string host, int port, TailscaleService tailscale, ILogger log)
        {
            using (client)
            {
                try
                {
                    await using var upstream = await tailscale.ConnectAsync(host, port, _cts.Token);
                    var local = client.GetStream();
                    using var done = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    // When either side closes, tear down both.
                    await Task.WhenAny(local.CopyToAsync(upstream, done.Token), upstream.CopyToAsync(local, done.Token));
                    done.Cancel();
                }
                catch (Exception ex) when (ex is IOException or SocketException or AppException or OperationCanceledException)
                {
                    log.LogDebug("RTSP forward to {Host}:{Port} ended: {Error}", host, port, ex.Message);
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}

/// <summary>Minimal SOCKS5 client (RFC 1928 CONNECT with RFC 1929 username/password auth).</summary>
internal static class Socks5
{
    public static async Task<Socket> ConnectAsync(TailnetProxy proxy, string host, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var t = timeout.Token;

            await socket.ConnectAsync(proxy.Host, proxy.Port, t);
            using var s = new NetworkStream(socket, ownsSocket: false);

            await s.WriteAsync(new byte[] { 5, 1, 2 }, t); // SOCKS5, one method offered: username/password
            var choice = await ReadExactAsync(s, 2, t);
            if (choice[1] != 2) throw new IOException("the Tailscale proxy rejected authentication");

            var user = "tsnet"u8.ToArray();
            var pass = Encoding.UTF8.GetBytes(proxy.Password);
            await s.WriteAsync((byte[])[1, (byte)user.Length, .. user, (byte)pass.Length, .. pass], t);
            if ((await ReadExactAsync(s, 2, t))[1] != 0) throw new IOException("the Tailscale proxy rejected its credentials");

            byte[] address = IPAddress.TryParse(host, out var ip)
                ? [ip.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1, .. ip.GetAddressBytes()]
                : [3, (byte)Encoding.ASCII.GetByteCount(host), .. Encoding.ASCII.GetBytes(host)];
            await s.WriteAsync((byte[])[5, 1, 0, .. address, (byte)(port >> 8), (byte)port], t);

            var reply = await ReadExactAsync(s, 4, t);
            if (reply[1] != 0) throw new IOException($"Tailscale couldn't connect to {host}:{port} ({Describe(reply[1])})");
            var rest = reply[3] switch
            {
                1 => 4 + 2,
                4 => 16 + 2,
                3 => (await ReadExactAsync(s, 1, t))[0] + 2,
                _ => throw new IOException("the Tailscale proxy sent an invalid reply"),
            };
            await ReadExactAsync(s, rest, t);
            return socket;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new IOException($"Timed out connecting to {host}:{port} through Tailscale");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReadExactAsync(Stream s, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await s.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    private static string Describe(byte code) => code switch
    {
        2 => "not allowed by the tailnet's access rules",
        3 => "network unreachable: is the subnet route approved and the router online?",
        4 => "host unreachable",
        5 => "connection refused",
        6 => "timed out",
        _ => $"SOCKS error {code}",
    };
}
