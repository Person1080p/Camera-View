using System.Globalization;
using System.Net;
using System.Xml.Linq;

namespace CameraViewer.Api.Services;

public sealed record SystemStatus(long UptimeSeconds, double? CpuPercent, double? MemoryUsedMb, double? MemoryAvailableMb);
public sealed record NtpSettings(string Host, int Port, int IntervalMinutes);

/// <summary>Times are ISO 8601 with offsets. Drift compares wall clocks, since that is what recordings are stamped with.</summary>
public sealed record ClockStatus(string DeviceTime, string TimeMode, string TimeZone, string ServerTime, double DriftSeconds, NtpSettings? Ntp);

public sealed record NetworkStatus(string? IpAddress, string? SubnetMask, string? Gateway, string? PrimaryDns, string? SecondaryDns, string? MacAddress, string? Addressing);
/// <summary>Temperature, power-on days and health come from the disk's SMART data when the device reports it.</summary>
public sealed record DiskStatus(int Id, string Name, string Type, string Status, long CapacityMb, long FreeMb, string Property,
    int? TemperatureC, int? PowerOnDays, string? Health);
public sealed record ChannelStatus(int Id, string Name, string? IpAddress, int? Port, bool Online, string? Detail);

public sealed record NvrStatus(
    DeviceInfo? Info, SystemStatus? System, ClockStatus? Clock, NetworkStatus? Network,
    List<DiskStatus>? Disks, string? StorageMode, List<ChannelStatus>? Channels, List<string> Errors);

/// <summary>MaxFrameRate is in 1/100 fps as the device reports it; 0 means "full frame rate".</summary>
public sealed record StreamSettings(string Codec, string Resolution, string BitrateMode, int Quality, int BitrateKbps, int MaxFrameRate);
public sealed record StreamOptions(string[] Codecs, string[] Resolutions, string[] BitrateModes, int[] Qualities, int[] FrameRates, int MinBitrateKbps, int MaxBitrateKbps);
public sealed record StreamConfig(StreamSettings Settings, StreamOptions Options);

public sealed record OverlaySettings(bool ShowDateTime, string DateStyle, string TimeStyle, bool ShowWeekday, bool ShowChannelName);
public sealed record OverlayOptions(string[] DateStyles, string[] TimeStyles);

public sealed record CameraSettings(
    int Channel, string Name, string? IpAddress, OverlaySettings? Overlay, OverlayOptions? OverlayOptions,
    StreamConfig? Main, StreamConfig? Sub, List<string> Errors);

/// <summary>Time is device-local wall clock. Channel is set for per-camera events.</summary>
public sealed record LogEntry(string Time, string Major, string Minor, int? Channel, string? User, string? IpAddress, string? Detail);
public sealed record LogPage(List<LogEntry> Items, bool HasMore);

public sealed record NvrUser(int Id, string UserName, string Level, bool BuiltIn);

/// <summary>Addressing is "static" or "dhcp".</summary>
public sealed record IpSettings(string Addressing, string IpAddress, string SubnetMask, string Gateway, string PrimaryDns, string SecondaryDns);
public sealed record PortSettings(int HttpPort, bool HttpsEnabled, int HttpsPort, int RtspPort, int ServerPort);
public sealed record UpnpPort(string Service, int ExternalPort, bool Enabled);
/// <summary>The DDNS password is write-only on the device, so it is never returned.</summary>
public sealed record DdnsInfo(bool Enabled, string Provider, string[] Providers, string Server, int Port, string Domain, string UserName, string? Status);

/// <summary>EZVIZ / Hik-Connect. The verification code is write-only on the device.</summary>
public sealed record CloudInfo(bool Enabled, string Server, bool Registered);

public sealed record RemoteAccess(bool UpnpEnabled, List<UpnpPort> UpnpPorts, bool? SshEnabled, DdnsInfo? Ddns, CloudInfo? Cloud);

/// <summary>A null or empty Password keeps the one stored on the device.</summary>
public sealed record DdnsUpdate(bool Enabled, string Provider, string Server, int Port, string Domain, string UserName, string? Password);

/// <summary>A null or empty VerificationCode keeps the one stored on the device.</summary>
public sealed record CloudUpdate(bool Enabled, string Server, string? VerificationCode);
public sealed record NetworkSettings(IpSettings? Ip, string? MacAddress, PortSettings? Ports, RemoteAccess? Remote, List<string> Errors);
public sealed record SystemSettings(string DeviceName, bool? OverwriteWhenFull);

/// <summary>
/// Administration of Hikvision-style NVRs (and standalone cameras) over ISAPI. Every change re-reads the
/// device's current document and edits only the relevant fields before writing it back, so settings this
/// app doesn't model are preserved.
/// </summary>
public class NvrAdmin(IsapiClient isapi)
{
    public static readonly string[] UserLevels = ["Administrator", "Operator", "Viewer"];
    public static readonly string[] LogTypes = ["Alarm", "Exception", "Operation", "Information"];

    // ---------- status ----------

    public async Task<NvrStatus> GetStatusAsync(DeviceConnection c, CancellationToken ct)
    {
        var errors = new List<string>();
        async Task<T?> Try<T>(string what, Func<Task<T>> load) where T : class
        {
            try { return await load(); }
            catch (IsapiException ex)
            {
                lock (errors) errors.Add($"{what}: {ex.Message}");
                return null;
            }
        }

        var info = Try("Device info", () => isapi.GetDeviceInfoAsync(c, ct));
        var system = Try("System", () => GetSystemAsync(c, ct));
        var clock = Try("Clock", () => GetClockAsync(c, ct));
        var network = Try("Network", () => GetNetworkAsync(c, ct));
        var storage = Try("Storage", () => GetStorageAsync(c, ct));
        var channels = Try("Channels", () => GetChannelStatusAsync(c, ct));
        await Task.WhenAll(info, system, clock, network, storage, channels);

        return new NvrStatus(info.Result, system.Result, clock.Result, network.Result,
            storage.Result?.Disks, storage.Result?.Mode, channels.Result, errors);
    }

    private async Task<SystemStatus> GetSystemAsync(DeviceConnection c, CancellationToken ct)
    {
        var root = (await isapi.GetXmlAsync(c, "/ISAPI/System/status", ct)).Root!;
        return new SystemStatus(
            Long(root.Val("deviceUpTime")),
            Double(root.Els("cpuUtilization").FirstOrDefault()?.Value),
            Double(root.Els("memoryUsage").FirstOrDefault()?.Value),
            Double(root.Els("memoryAvailable").FirstOrDefault()?.Value));
    }

    public async Task<ClockStatus> GetClockAsync(DeviceConnection c, CancellationToken ct)
    {
        var time = (await isapi.GetXmlAsync(c, "/ISAPI/System/time", ct)).Root!;
        var deviceText = time.Val("localTime") ?? "";
        var now = DateTimeOffset.Now;
        var drift = DateTimeOffset.TryParse(deviceText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var device)
            ? (device.DateTime - now.DateTime).TotalSeconds
            : 0;

        NtpSettings? ntp = null;
        try
        {
            var server = (await isapi.GetXmlAsync(c, "/ISAPI/System/time/ntpServers/1", ct)).Root!;
            var host = server.Val("addressingFormatType") == "ipaddress" ? server.Val("ipAddress") : server.Val("hostName");
            ntp = new NtpSettings(host ?? "", (int)Long(server.Val("portNo"), 123), (int)Long(server.Val("synchronizeInterval"), 60));
        }
        catch (IsapiException)
        {
            // NTP settings are optional; some devices don't expose them.
        }

        return new ClockStatus(deviceText, time.Val("timeMode") ?? "", time.Val("timeZone") ?? "",
            now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), Math.Round(drift), ntp);
    }

    private async Task<NetworkStatus> GetNetworkAsync(DeviceConnection c, CancellationToken ct)
    {
        var xml = await isapi.GetXmlAsync(c, "/ISAPI/System/Network/interfaces", ct);
        var nic = xml.Els("NetworkInterface").FirstOrDefault() ?? throw new IsapiException("No network interface reported");
        var ip = nic.El("IPAddress");
        return new NetworkStatus(
            ip?.Val("ipAddress"), ip?.Val("subnetMask"),
            ip?.El("DefaultGateway")?.Val("ipAddress"),
            ip?.El("PrimaryDNS")?.Val("ipAddress"), ip?.El("SecondaryDNS")?.Val("ipAddress"),
            nic.Els("MACAddress").FirstOrDefault()?.Value, ip?.Val("addressingType"));
    }

    private sealed record StorageResult(List<DiskStatus> Disks, string? Mode);

    private async Task<StorageResult> GetStorageAsync(DeviceConnection c, CancellationToken ct)
    {
        var xml = await isapi.GetXmlAsync(c, "/ISAPI/ContentMgmt/Storage", ct);
        var disks = await Task.WhenAll(xml.Els("hdd").Select(async h =>
        {
            var id = (int)Long(h.Val("id"));
            XElement? smart = null;
            try { smart = (await isapi.GetXmlAsync(c, $"/ISAPI/ContentMgmt/Storage/hdd/{id}/SMARTTest/status", ct)).Root; }
            catch (IsapiException) { }
            // (sic) Hikvision's element names are misspelled.
            return new DiskStatus(id, h.Val("hddName") ?? "", h.Val("hddType") ?? "", h.Val("status") ?? "",
                Long(h.Val("capacity")), Long(h.Val("freeSpace")), h.Val("property") ?? "",
                smart?.Val("temprature") is { } t ? (int)Long(t) : null,
                smart?.Val("powerOnDay") is { } d ? (int)Long(d) : null,
                smart?.Val("allEvaluaingStatus") ?? smart?.Val("selfEvaluaingStatus"));
        }));
        return new StorageResult(disks.ToList(), xml.Root?.Val("workMode"));
    }

    // ---------- network ----------

    private const string IpPath = "/ISAPI/System/Network/interfaces/1/ipAddress";
    private const string PortsPath = "/ISAPI/Security/adminAccesses";

    public async Task<NetworkSettings> GetNetworkSettingsAsync(DeviceConnection c, CancellationToken ct)
    {
        var errors = new List<string>();
        async Task<XElement?> Try(string what, string path)
        {
            try { return (await isapi.GetXmlAsync(c, path, ct)).Root; }
            catch (IsapiException ex)
            {
                lock (errors) errors.Add($"{what}: {ex.Message}");
                return null;
            }
        }
        // These are optional features; a device without them just doesn't show the setting.
        async Task<XElement?> Optional(string path)
        {
            try { return (await isapi.GetXmlAsync(c, path, ct)).Root; }
            catch (IsapiException) { return null; }
        }

        var nic = Try("Network interface", "/ISAPI/System/Network/interfaces/1");
        var ports = Try("Ports", PortsPath);
        var upnp = Optional("/ISAPI/System/Network/UPnP");
        var ssh = Optional("/ISAPI/System/Network/ssh");
        var ddns = Optional(DdnsPath);
        var ddnsCaps = Optional("/ISAPI/System/Network/DDNS/capabilities");
        var cloud = Optional(CloudPath);
        await Task.WhenAll(nic, ports, upnp, ssh, ddns, ddnsCaps, cloud);

        IpSettings? ip = null;
        if (nic.Result?.El("IPAddress") is { } a)
            ip = new IpSettings(a.Val("addressingType") ?? "static", a.Val("ipAddress") ?? "", a.Val("subnetMask") ?? "",
                a.El("DefaultGateway")?.Val("ipAddress") ?? "", a.El("PrimaryDNS")?.Val("ipAddress") ?? "",
                a.El("SecondaryDNS")?.Val("ipAddress") ?? "");

        PortSettings? portSettings = null;
        if (ports.Result is { } p)
        {
            XElement? Proto(string name) => p.Els("AdminAccessProtocol").FirstOrDefault(e => e.Val("protocol") == name);
            int Port(string name, int fallback) => (int)Long(Proto(name)?.Val("portNo"), fallback);
            portSettings = new PortSettings(Port("HTTP", 80), Proto("HTTPS")?.Val("enabled") == "true", Port("HTTPS", 443),
                Port("RTSP", 554), Port("DEV_MANAGE", 8000));
        }

        var d = ddns.Result;
        var ez = cloud.Result;
        var remote = new RemoteAccess(
            upnp.Result?.Val("enabled") == "true",
            upnp.Result?.Els("port").Select(x => new UpnpPort(x.Val("internalPort") ?? "", (int)Long(x.Val("externalPort")), x.Val("enabled") == "true")).ToList() ?? [],
            ssh.Result is null ? null : ssh.Result.Val("enabled") == "true",
            d is null ? null : new DdnsInfo(d.Val("enabled") == "true", d.Val("provider") ?? "",
                ddnsCaps.Result?.Els("provider").FirstOrDefault().Opts() ?? [],
                ServerAddress(d.El("serverAddress")), (int)Long(d.Val("portNo")),
                d.Val("deviceDomainName") ?? "", d.Val("userName") ?? "", NullIfEmpty(d.Val("status"))),
            ez is null ? null : new CloudInfo(ez.Val("enabled") == "true", ServerAddress(ez.El("serverAddress")), ez.Val("registerStatus") == "true"));

        return new NetworkSettings(ip, nic.Result?.Els("MACAddress").FirstOrDefault()?.Value, portSettings, remote, errors);
    }

    /// <summary>Returns true if the device says it must reboot for the change to apply.</summary>
    public async Task<bool> SetIpAsync(DeviceConnection c, IpSettings s, CancellationToken ct)
    {
        var xml = await isapi.GetXmlAsync(c, IpPath, ct);
        var a = xml.Root!;
        a.SetVal("addressingType", s.Addressing);
        if (s.Addressing == "static")
        {
            a.SetVal("ipAddress", s.IpAddress);
            a.SetVal("subnetMask", s.SubnetMask);
            SetNested(a, "DefaultGateway", s.Gateway);
        }
        SetNested(a, "PrimaryDNS", s.PrimaryDns);
        SetNested(a, "SecondaryDNS", s.SecondaryDns);
        return NeedsReboot(await isapi.SendXmlAsync(c, HttpMethod.Put, IpPath, xml, ct));
    }

    public async Task<bool> SetPortsAsync(DeviceConnection c, PortSettings s, CancellationToken ct)
    {
        var xml = await isapi.GetXmlAsync(c, PortsPath, ct);
        foreach (var proto in xml.Els("AdminAccessProtocol"))
        {
            switch (proto.Val("protocol"))
            {
                case "HTTP": proto.SetVal("portNo", s.HttpPort.ToString(CultureInfo.InvariantCulture)); break;
                case "HTTPS":
                    proto.SetVal("enabled", Bool(s.HttpsEnabled));
                    proto.SetVal("portNo", s.HttpsPort.ToString(CultureInfo.InvariantCulture));
                    break;
                case "RTSP": proto.SetVal("portNo", s.RtspPort.ToString(CultureInfo.InvariantCulture)); break;
                case "DEV_MANAGE": proto.SetVal("portNo", s.ServerPort.ToString(CultureInfo.InvariantCulture)); break;
            }
        }
        return NeedsReboot(await isapi.SendXmlAsync(c, HttpMethod.Put, PortsPath, xml, ct));
    }

    /// <summary>Null leaves a setting unchanged.</summary>
    public async Task<bool> SetRemoteAccessAsync(DeviceConnection c, bool? upnp, bool? ssh, CancellationToken ct)
    {
        var reboot = false;
        if (upnp is { } u)
        {
            var xml = await isapi.GetXmlAsync(c, "/ISAPI/System/Network/UPnP", ct);
            xml.Root!.SetVal("enabled", Bool(u));
            reboot |= NeedsReboot(await isapi.SendXmlAsync(c, HttpMethod.Put, "/ISAPI/System/Network/UPnP", xml, ct));
        }
        if (ssh is { } s)
        {
            var xml = await isapi.GetXmlAsync(c, "/ISAPI/System/Network/ssh", ct);
            xml.Root!.SetVal("enabled", Bool(s));
            reboot |= NeedsReboot(await isapi.SendXmlAsync(c, HttpMethod.Put, "/ISAPI/System/Network/ssh", xml, ct));
        }
        return reboot;
    }

    private const string DdnsPath = "/ISAPI/System/Network/DDNS/1";
    private const string CloudPath = "/ISAPI/System/Network/EZVIZ";

    public async Task<bool> SetDdnsAsync(DeviceConnection c, DdnsUpdate s, CancellationToken ct)
    {
        var caps = (await isapi.GetXmlAsync(c, "/ISAPI/System/Network/DDNS/capabilities", ct)).Els("provider").FirstOrDefault().Opts();
        Require(caps, s.Provider, "DDNS provider");

        var xml = await isapi.GetXmlAsync(c, DdnsPath, ct);
        var d = xml.Root!;
        d.El("status")?.Remove(); // read-only
        d.SetVal("enabled", Bool(s.Enabled));
        d.SetVal("provider", s.Provider);
        SetServerAddress(d, s.Server);
        d.SetVal("portNo", s.Port.ToString(CultureInfo.InvariantCulture));
        d.SetVal("deviceDomainName", s.Domain);
        d.SetVal("userName", s.UserName);
        // Omitting the password keeps the stored one (verified on this firmware for device accounts).
        d.El("password")?.Remove();
        if (!string.IsNullOrEmpty(s.Password)) d.El("userName")!.AddAfterSelf(new XElement(d.Name.Namespace + "password", s.Password));
        return NeedsReboot(await isapi.SendXmlAsync(c, HttpMethod.Put, DdnsPath, xml, ct));
    }

    public async Task<bool> SetCloudAsync(DeviceConnection c, CloudUpdate s, CancellationToken ct)
    {
        var xml = await isapi.GetXmlAsync(c, CloudPath, ct);
        var e = xml.Root!;
        e.El("registerStatus")?.Remove(); // read-only
        e.SetVal("enabled", Bool(s.Enabled));
        SetServerAddress(e, s.Server);
        // The device reports the code as empty; sending that back would erase it, so only send a new one.
        e.El("verificationCode")?.Remove();
        if (!string.IsNullOrEmpty(s.VerificationCode)) e.Add(new XElement(e.Name.Namespace + "verificationCode", s.VerificationCode));
        return NeedsReboot(await isapi.SendXmlAsync(c, HttpMethod.Put, CloudPath, xml, ct));
    }

    private static string ServerAddress(XElement? address) =>
        address is null ? "" : address.Val("addressingFormatType") == "ipaddress" ? address.Val("ipAddress") ?? "" : address.Val("hostName") ?? "";

    private static void SetServerAddress(XElement parent, string host)
    {
        var address = parent.El("serverAddress");
        if (address is null)
        {
            address = new XElement(parent.Name.Namespace + "serverAddress");
            parent.Add(address);
        }
        var isIp = IPAddress.TryParse(host, out _);
        address.SetVal("addressingFormatType", isIp ? "ipaddress" : "hostname");
        address.SetVal(isIp ? "ipAddress" : "hostName", host);
    }

    // ---------- system ----------

    public async Task<SystemSettings> GetSystemSettingsAsync(DeviceConnection c, CancellationToken ct)
    {
        var info = (await isapi.GetXmlAsync(c, "/ISAPI/System/deviceInfo", ct)).Root!;
        bool? overwrite = null;
        try { overwrite = (await isapi.GetXmlAsync(c, "/ISAPI/ContentMgmt/Storage/extension", ct)).Root!.Val("LoopEnable") == "true"; }
        catch (IsapiException) { }
        return new SystemSettings(info.Val("deviceName") ?? "", overwrite);
    }

    public async Task<bool> SetSystemSettingsAsync(DeviceConnection c, SystemSettings s, CancellationToken ct)
    {
        var reboot = false;
        var info = await isapi.GetXmlAsync(c, "/ISAPI/System/deviceInfo", ct);
        if (info.Root!.Val("deviceName") != s.DeviceName)
        {
            info.Root!.SetVal("deviceName", s.DeviceName);
            reboot |= NeedsReboot(await isapi.SendXmlAsync(c, HttpMethod.Put, "/ISAPI/System/deviceInfo", info, ct));
        }
        if (s.OverwriteWhenFull is { } overwrite)
        {
            var ext = await isapi.GetXmlAsync(c, "/ISAPI/ContentMgmt/Storage/extension", ct);
            if (ext.Root!.Val("LoopEnable") != Bool(overwrite))
            {
                ext.Root!.SetVal("LoopEnable", Bool(overwrite));
                reboot |= NeedsReboot(await isapi.SendXmlAsync(c, HttpMethod.Put, "/ISAPI/ContentMgmt/Storage/extension", ext, ct));
            }
        }
        return reboot;
    }

    private static void SetNested(XElement parent, string name, string ip)
    {
        var el = parent.El(name);
        if (el is null)
        {
            el = new XElement(parent.Name.Namespace + name);
            parent.Add(el);
        }
        el.SetVal("ipAddress", ip);
    }

    private static bool NeedsReboot(XDocument response) => response.Root?.Val("statusCode") == "7";

    private async Task<List<ChannelStatus>> GetChannelStatusAsync(DeviceConnection c, CancellationToken ct)
    {
        var proxy = await isapi.GetXmlAsync(c, "/ISAPI/ContentMgmt/InputProxy/channels", ct);
        Dictionary<string, (bool Online, string? Detail)> status = [];
        try
        {
            status = (await isapi.GetXmlAsync(c, "/ISAPI/ContentMgmt/InputProxy/channels/status", ct))
                .Els("InputProxyChannelStatus")
                .ToDictionary(e => e.Val("id") ?? "", e => (e.Val("online") == "true", e.Val("chanDetectResult")));
        }
        catch (IsapiException) { }

        return proxy.Els("InputProxyChannel").Select(e =>
        {
            var id = e.Val("id") ?? "";
            var src = e.El("sourceInputPortDescriptor");
            var s = status.GetValueOrDefault(id);
            return new ChannelStatus((int)Long(id), e.Val("name") ?? $"Channel {id}", src?.Val("ipAddress"),
                src?.Val("managePortNo") is { } p ? (int)Long(p) : null, s.Online, s.Detail);
        }).OrderBy(x => x.Id).ToList();
    }

    // ---------- maintenance ----------

    /// <summary>Sets the device clock to this server's wall-clock time, keeping the device's time zone.</summary>
    public async Task<ClockStatus> SyncClockAsync(DeviceConnection c, CancellationToken ct)
    {
        var xml = await isapi.GetXmlAsync(c, "/ISAPI/System/time", ct);
        var root = xml.Root!;
        var offset = DateTimeOffset.TryParse(root.Val("localTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var current)
            ? current.Offset
            : DateTimeOffset.Now.Offset;
        // Strip the server's own offset: the device should show our wall-clock time in its own zone.
        var wall = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified);
        root.SetVal("timeMode", "manual");
        root.SetVal("localTime", new DateTimeOffset(wall, offset).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        await isapi.SendXmlAsync(c, HttpMethod.Put, "/ISAPI/System/time", xml, ct);
        return await GetClockAsync(c, ct);
    }

    /// <summary>Updates time mode ("manual" or "NTP"), the POSIX-style time zone, and optionally the NTP server.</summary>
    public async Task<ClockStatus> SetTimeConfigAsync(DeviceConnection c, string timeMode, string timeZone, NtpSettings? ntp, CancellationToken ct)
    {
        if (ntp is not null)
        {
            var serverXml = await isapi.GetXmlAsync(c, "/ISAPI/System/time/ntpServers/1", ct);
            var server = serverXml.Root!;
            var isIp = IPAddress.TryParse(ntp.Host, out _);
            server.SetVal("addressingFormatType", isIp ? "ipaddress" : "hostname");
            server.SetVal(isIp ? "ipAddress" : "hostName", ntp.Host);
            server.SetVal("portNo", ntp.Port.ToString(CultureInfo.InvariantCulture));
            server.SetVal("synchronizeInterval", ntp.IntervalMinutes.ToString(CultureInfo.InvariantCulture));
            await isapi.SendXmlAsync(c, HttpMethod.Put, "/ISAPI/System/time/ntpServers/1", serverXml, ct);
        }

        var xml = await isapi.GetXmlAsync(c, "/ISAPI/System/time", ct);
        var root = xml.Root!;
        root.SetVal("timeMode", timeMode);
        root.SetVal("timeZone", timeZone);
        // localTime is only meaningful in manual mode; leaving the device's own value avoids moving the clock.
        await isapi.SendXmlAsync(c, HttpMethod.Put, "/ISAPI/System/time", xml, ct);
        return await GetClockAsync(c, ct);
    }

    public Task RebootAsync(DeviceConnection c, CancellationToken ct) =>
        isapi.SendXmlAsync(c, HttpMethod.Put, "/ISAPI/System/reboot", null, ct);

    public Task RebootCameraAsync(DeviceConnection c, int channel, CancellationToken ct) =>
        isapi.SendXmlAsync(c, HttpMethod.Put, $"/ISAPI/ContentMgmt/InputProxy/channels/{channel}/reboot", null, ct);

    // ---------- camera settings ----------

    // NVRs proxy camera settings under ContentMgmt; standalone cameras expose them directly.
    private static string[] ChannelPaths(int ch) =>
        [$"/ISAPI/ContentMgmt/InputProxy/channels/{ch}", $"/ISAPI/System/Video/inputs/channels/{ch}"];
    private static string[] OverlayPaths(int ch) =>
        [$"/ISAPI/ContentMgmt/InputProxy/channels/{ch}/video/overlays", $"/ISAPI/System/Video/inputs/channels/{ch}/overlays"];
    private static string[] StreamPaths(int ch, bool sub) =>
    [
        $"/ISAPI/ContentMgmt/StreamingProxy/channels/{IsapiClient.StreamId(ch, sub)}",
        $"/ISAPI/Streaming/channels/{IsapiClient.StreamId(ch, sub)}",
    ];

    private async Task<(string Path, XDocument Xml)> GetFirstAsync(DeviceConnection c, string[] paths, CancellationToken ct)
    {
        IsapiException? last = null;
        foreach (var path in paths)
        {
            try { return (path, await isapi.GetXmlAsync(c, path, ct)); }
            catch (IsapiException ex) { last = ex; }
        }
        throw last!;
    }

    public async Task<CameraSettings> GetCameraSettingsAsync(DeviceConnection c, int ch, CancellationToken ct)
    {
        var errors = new List<string>();
        async Task<T?> Try<T>(string what, Func<Task<T>> load) where T : class
        {
            try { return await load(); }
            catch (IsapiException ex)
            {
                lock (errors) errors.Add($"{what}: {ex.Message}");
                return null;
            }
        }

        var channel = Try("Channel", async () => (await GetFirstAsync(c, ChannelPaths(ch), ct)).Xml.Root!);
        var overlay = Try("On-screen display", () => GetOverlayAsync(c, ch, ct));
        var main = Try("Main stream", () => GetStreamAsync(c, ch, false, ct));
        var sub = Try("Sub stream", () => GetStreamAsync(c, ch, true, ct));
        await Task.WhenAll(channel, overlay, main, sub);

        var ch0 = channel.Result;
        return new CameraSettings(ch, ch0?.Val("name") ?? $"Channel {ch}",
            ch0?.El("sourceInputPortDescriptor")?.Val("ipAddress"),
            overlay.Result?.Settings, overlay.Result?.Options, main.Result, sub.Result, errors);
    }

    public async Task RenameChannelAsync(DeviceConnection c, int ch, string name, CancellationToken ct)
    {
        var (path, xml) = await GetFirstAsync(c, ChannelPaths(ch), ct);
        xml.Root!.SetVal("name", name);
        await isapi.SendXmlAsync(c, HttpMethod.Put, path, xml, ct);
    }

    private sealed record OverlayResult(OverlaySettings Settings, OverlayOptions Options);

    private async Task<OverlayResult> GetOverlayAsync(DeviceConnection c, int ch, CancellationToken ct)
    {
        var (path, xml) = await GetFirstAsync(c, OverlayPaths(ch), ct);
        XElement? caps = null;
        try { caps = (await isapi.GetXmlAsync(c, path + "/capabilities", ct)).Root; }
        catch (IsapiException) { }

        var dt = xml.Root!.El("DateTimeOverlay");
        var name = xml.Root!.El("channelNameOverlay");
        var capDt = caps?.El("DateTimeOverlay");
        return new OverlayResult(
            new OverlaySettings(dt?.Val("enabled") == "true", dt?.Val("dateStyle") ?? "", dt?.Val("timeStyle") ?? "",
                dt?.Val("displayWeek") == "true", name?.Val("enabled") == "true"),
            new OverlayOptions(capDt?.El("dateStyle").Opts() ?? [], capDt?.El("timeStyle").Opts() ?? []));
    }

    public async Task SetOverlayAsync(DeviceConnection c, int ch, OverlaySettings s, CancellationToken ct)
    {
        var current = await GetOverlayAsync(c, ch, ct);
        Require(current.Options.DateStyles, s.DateStyle, "date format");
        Require(current.Options.TimeStyles, s.TimeStyle, "time format");

        var (path, xml) = await GetFirstAsync(c, OverlayPaths(ch), ct);
        if (xml.Root!.El("DateTimeOverlay") is { } dt)
        {
            dt.SetVal("enabled", Bool(s.ShowDateTime));
            dt.SetVal("dateStyle", s.DateStyle);
            dt.SetVal("timeStyle", s.TimeStyle);
            dt.SetVal("displayWeek", Bool(s.ShowWeekday));
        }
        xml.Root!.El("channelNameOverlay")?.SetVal("enabled", Bool(s.ShowChannelName));
        await isapi.SendXmlAsync(c, HttpMethod.Put, path, xml, ct);
    }

    private async Task<StreamConfig> GetStreamAsync(DeviceConnection c, int ch, bool sub, CancellationToken ct)
    {
        var (path, xml) = await GetFirstAsync(c, StreamPaths(ch, sub), ct);
        var video = xml.Root!.El("Video") ?? throw new IsapiException("No video settings reported");
        var capVideo = (await isapi.GetXmlAsync(c, path + "/capabilities", ct)).Root!.El("Video")
            ?? throw new IsapiException("No video capabilities reported");

        var mode = video.Val("videoQualityControlType") ?? "VBR";
        var bitrate = Long(mode == "CBR" ? video.Val("constantBitRate") : video.Val("vbrUpperCap"));

        var widths = capVideo.El("videoResolutionWidth").Opts();
        var heights = capVideo.El("videoResolutionHeight").Opts();
        // Capabilities list widths and heights as parallel lists.
        var resolutions = widths.Zip(heights, (w, h) => $"{w}x{h}").Distinct().ToArray();

        var rateCap = capVideo.El(mode == "CBR" ? "constantBitRate" : "vbrUpperCap") ?? capVideo.El("vbrUpperCap");
        return new StreamConfig(
            new StreamSettings(video.Val("videoCodecType") ?? "",
                $"{video.Val("videoResolutionWidth")}x{video.Val("videoResolutionHeight")}",
                mode, (int)Long(video.Val("fixedQuality")), (int)bitrate, (int)Long(video.Val("maxFrameRate"))),
            new StreamOptions(
                capVideo.El("videoCodecType").Opts(), resolutions, capVideo.El("videoQualityControlType").Opts(),
                capVideo.El("fixedQuality").Opts().Select(q => (int)Long(q)).ToArray(),
                capVideo.El("maxFrameRate").Opts().Select(f => (int)Long(f)).ToArray(),
                (int)Long(rateCap?.Attribute("min")?.Value, 32), (int)Long(rateCap?.Attribute("max")?.Value, 16384)));
    }

    public async Task SetStreamAsync(DeviceConnection c, int ch, bool sub, StreamSettings s, CancellationToken ct)
    {
        var current = await GetStreamAsync(c, ch, sub, ct);
        var o = current.Options;
        Require(o.Codecs, s.Codec, "codec");
        Require(o.Resolutions, s.Resolution, "resolution");
        Require(o.BitrateModes, s.BitrateMode, "bitrate mode");
        Require(o.Qualities.Select(q => q.ToString()).ToArray(), s.Quality.ToString(), "quality");
        Require(o.FrameRates.Select(f => f.ToString()).ToArray(), s.MaxFrameRate.ToString(), "frame rate");
        if (s.BitrateKbps < o.MinBitrateKbps || s.BitrateKbps > o.MaxBitrateKbps)
            throw new AppException($"Bitrate must be between {o.MinBitrateKbps} and {o.MaxBitrateKbps} Kbps");

        var (path, xml) = await GetFirstAsync(c, StreamPaths(ch, sub), ct);
        var video = xml.Root!.El("Video")!;
        var size = s.Resolution.Split('x');
        video.SetVal("videoCodecType", s.Codec);
        video.SetVal("videoResolutionWidth", size[0]);
        video.SetVal("videoResolutionHeight", size[1]);
        video.SetVal("videoQualityControlType", s.BitrateMode);
        video.SetVal("fixedQuality", s.Quality.ToString(CultureInfo.InvariantCulture));
        video.SetVal(s.BitrateMode == "CBR" ? "constantBitRate" : "vbrUpperCap", s.BitrateKbps.ToString(CultureInfo.InvariantCulture));
        video.SetVal("maxFrameRate", s.MaxFrameRate.ToString(CultureInfo.InvariantCulture));
        await isapi.SendXmlAsync(c, HttpMethod.Put, path, xml, ct);
    }

    // ---------- logs ----------

    /// <summary>Searches the device log. <paramref name="type"/> is null for all, or one of <see cref="LogTypes"/>.</summary>
    public async Task<LogPage> SearchLogsAsync(DeviceConnection c, DateTime from, DateTime to, string? type, int page, int pageSize, CancellationToken ct)
    {
        // Hikvision spells this category "Infomation".
        var metaId = type is null ? "log.std-cgi.com" : $"log.std-cgi.com/{(type == "Information" ? "Infomation" : type)}";
        // This request is accepted without a namespace (matching what the device was tested with).
        var ns = XNamespace.None;
        var body = new XDocument(new XElement(ns + "CMSearchDescription",
            new XElement(ns + "searchID", Guid.NewGuid().ToString().ToUpperInvariant()),
            new XElement(ns + "metaId", metaId),
            new XElement(ns + "timeSpanList", new XElement(ns + "timeSpan",
                new XElement(ns + "startTime", from.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                new XElement(ns + "endTime", to.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)))),
            new XElement(ns + "maxResults", pageSize),
            new XElement(ns + "searchResultPostion", page * pageSize)));

        var result = await isapi.SendXmlAsync(c, HttpMethod.Post, "/ISAPI/ContentMgmt/logSearch", body, ct);
        var items = result.Els("logDescriptor").Select(d =>
        {
            // e.g. "log.std-cgi.com/Alarm/motionStart/6"
            var parts = (d.Val("metaId") ?? "").Split('/');
            var major = parts.ElementAtOrDefault(1) ?? "";
            int? channel = parts.Length > 3 && int.TryParse(parts[3], out var n) ? n : null;
            var time = (d.Val("StartDateTime") ?? "").TrimEnd('Z');
            return new LogEntry(time, major == "Infomation" ? "Information" : major, parts.ElementAtOrDefault(2) ?? "",
                channel, NullIfEmpty(d.Val("userName")), d.Val("ipAddress") is { } ip && ip != "0.0.0.0" ? ip : null,
                NullIfEmpty(d.Val("paraType")));
        }).ToList();

        var more = string.Equals(result.Root?.Val("responseStatusStrg"), "MORE", StringComparison.OrdinalIgnoreCase);
        return new LogPage(items, more);
    }

    // ---------- device accounts ----------

    public async Task<List<NvrUser>> GetUsersAsync(DeviceConnection c, CancellationToken ct) =>
        (await isapi.GetXmlAsync(c, "/ISAPI/Security/users", ct)).Els("User")
            .Select(u => new NvrUser((int)Long(u.Val("id")), u.Val("userName") ?? "", u.Val("userLevel") ?? "",
                u.El("attribute")?.Val("inherent") == "true"))
            .OrderBy(u => u.Id).ToList();

    public async Task CreateUserAsync(DeviceConnection c, string userName, string password, string level, CancellationToken ct)
    {
        var ns = XNamespace.Get("http://www.std-cgi.com/ver20/XMLSchema");
        var body = new XDocument(new XElement(ns + "User",
            new XAttribute("version", "1.0"),
            new XElement(ns + "userName", userName),
            new XElement(ns + "password", password),
            new XElement(ns + "userLevel", level)));
        await isapi.SendXmlAsync(c, HttpMethod.Post, "/ISAPI/Security/users", body, ct);
    }

    public async Task UpdateUserAsync(DeviceConnection c, int id, string? password, string? level, CancellationToken ct)
    {
        var xml = await isapi.GetXmlAsync(c, $"/ISAPI/Security/users/{id}", ct);
        var user = xml.Root!;
        if (level is not null) user.SetVal("userLevel", level);
        if (password is not null)
        {
            // The device expects the password right after the user name.
            user.El("password")?.Remove();
            var pw = new XElement(user.Name.Namespace + "password", password);
            if (user.El("userName") is { } name) name.AddAfterSelf(pw);
            else user.Add(pw);
        }
        await isapi.SendXmlAsync(c, HttpMethod.Put, $"/ISAPI/Security/users/{id}", xml, ct);
    }

    public Task DeleteUserAsync(DeviceConnection c, int id, CancellationToken ct) =>
        isapi.SendXmlAsync(c, HttpMethod.Delete, $"/ISAPI/Security/users/{id}", null, ct);

    // ---------- helpers ----------

    private static void Require(string[] options, string value, string what)
    {
        if (options.Length > 0 && !options.Contains(value))
            throw new AppException($"Unsupported {what} \"{value}\" (allowed: {string.Join(", ", options)})");
    }

    private static string Bool(bool b) => b ? "true" : "false";

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static long Long(string? s, long fallback = 0) =>
        long.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double? Double(string? s) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Round(v, 1) : null;
}
