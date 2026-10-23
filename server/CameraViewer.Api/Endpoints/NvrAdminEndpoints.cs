using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using CameraViewer.Api.Data;
using CameraViewer.Api.Services;

namespace CameraViewer.Api.Endpoints;

public record TimeConfigRequest(string TimeMode, string TimeZone, NtpSettings? Ntp);
public record RenameChannelRequest(string Name);
public record CreateNvrUserRequest(string UserName, string Password, string Level);
public record UpdateNvrUserRequest(string? Password, string? Level);
public record RemoteAccessRequest(bool? UpnpEnabled, bool? SshEnabled);

/// <summary>Result of a settings change; Note explains anything the admin still needs to do.</summary>
public record ChangeResult(bool RebootRequired, string? Note);

/// <summary>Administration of the NVRs/cameras themselves. Admin-only; every change is audit-logged.</summary>
public static partial class NvrAdminEndpoints
{
    private const int MaxDevicePasswordLength = 16;

    public static void MapNvrAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices/{id:int}/admin").RequireAuthorization("Admin");

        group.MapGet("/status", async (int id, HttpContext http, DeviceAccess access, NvrAdmin admin, CancellationToken ct) =>
            await admin.GetStatusAsync(await access.ConnectAsync(http.User, id, ct), ct));

        // ----- clock & maintenance -----

        group.MapPost("/clock/sync", async (int id, HttpContext http, DeviceAccess access, NvrAdmin admin, ILoggerFactory logs, CancellationToken ct) =>
        {
            var c = await access.ConnectAsync(http.User, id, ct);
            var before = await admin.GetClockAsync(c, ct);
            var after = await admin.SyncClockAsync(c, ct);
            Audit(logs, http, c, $"synced clock (was {before.DeviceTime}, now {after.DeviceTime})");
            return after;
        });

        group.MapPut("/clock", async (int id, TimeConfigRequest req, HttpContext http, DeviceAccess access, NvrAdmin admin,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            if (req.TimeMode is not ("manual" or "NTP"))
                throw new AppException("Time mode must be manual or NTP");
            if (string.IsNullOrWhiteSpace(req.TimeZone) || req.TimeZone.Length > 80)
                throw new AppException("A time zone is required");
            if (req.Ntp is { } ntp && (string.IsNullOrWhiteSpace(ntp.Host) || ntp.Host.Length > 128 ||
                    ntp.Port is < 1 or > 65535 || ntp.IntervalMinutes is < 1 or > 10080))
                throw new AppException("NTP needs a server, a port (1-65535) and an interval of 1-10080 minutes");

            var c = await access.ConnectAsync(http.User, id, ct);
            var result = await admin.SetTimeConfigAsync(c, req.TimeMode, req.TimeZone.Trim(),
                req.Ntp is null ? null : req.Ntp with { Host = req.Ntp.Host.Trim() }, ct);
            Audit(logs, http, c, $"set time mode {req.TimeMode}, zone {req.TimeZone}{(req.Ntp is null ? "" : $", NTP {req.Ntp.Host}")}");
            return result;
        });

        group.MapPost("/reboot", async (int id, HttpContext http, DeviceAccess access, NvrAdmin admin, ILoggerFactory logs, CancellationToken ct) =>
        {
            var c = await access.ConnectAsync(http.User, id, ct);
            await admin.RebootAsync(c, ct);
            access.ForgetChannels(id);
            Audit(logs, http, c, "rebooted the device");
            return Results.NoContent();
        });

        // ----- cameras -----

        group.MapGet("/channels/{ch:int}", async (int id, int ch, HttpContext http, DeviceAccess access, NvrAdmin admin, CancellationToken ct) =>
            await admin.GetCameraSettingsAsync(await access.ConnectAsync(http.User, id, ct), ch, ct));

        group.MapPost("/channels/{ch:int}/reboot", async (int id, int ch, HttpContext http, DeviceAccess access, NvrAdmin admin,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var c = await access.ConnectAsync(http.User, id, ct);
            await admin.RebootCameraAsync(c, ch, ct);
            Audit(logs, http, c, $"rebooted camera {ch}");
            return Results.NoContent();
        });

        group.MapPut("/channels/{ch:int}/name", async (int id, int ch, RenameChannelRequest req, HttpContext http, DeviceAccess access,
            NvrAdmin admin, ILoggerFactory logs, CancellationToken ct) =>
        {
            var name = (req.Name ?? "").Trim();
            if (name.Length is < 1 or > 32) throw new AppException("Camera names must be 1-32 characters");
            var c = await access.ConnectAsync(http.User, id, ct);
            await admin.RenameChannelAsync(c, ch, name, ct);
            access.ForgetChannels(id);
            Audit(logs, http, c, $"renamed camera {ch} to \"{name}\"");
            return Results.NoContent();
        });

        group.MapPut("/channels/{ch:int}/overlay", async (int id, int ch, OverlaySettings req, HttpContext http, DeviceAccess access,
            NvrAdmin admin, ILoggerFactory logs, CancellationToken ct) =>
        {
            var c = await access.ConnectAsync(http.User, id, ct);
            await admin.SetOverlayAsync(c, ch, req, ct);
            Audit(logs, http, c, $"changed on-screen display of camera {ch}");
            return Results.NoContent();
        });

        group.MapPut("/channels/{ch:int}/streams/{kind}", async (int id, int ch, string kind, StreamSettings req, HttpContext http,
            DeviceAccess access, NvrAdmin admin, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (kind is not ("main" or "sub")) return Results.NotFound();
            var c = await access.ConnectAsync(http.User, id, ct);
            await admin.SetStreamAsync(c, ch, kind == "sub", req, ct);
            Audit(logs, http, c, $"changed {kind} stream of camera {ch} to {req.Codec} {req.Resolution} {req.BitrateMode} {req.BitrateKbps}Kbps fps={req.MaxFrameRate}");
            return Results.NoContent();
        });

        // ----- network -----

        group.MapGet("/network", async (int id, HttpContext http, DeviceAccess access, NvrAdmin admin, CancellationToken ct) =>
            await admin.GetNetworkSettingsAsync(await access.ConnectAsync(http.User, id, ct), ct));

        group.MapPut("/network/ip", async (int id, IpSettings req, HttpContext http, DeviceAccess access, NvrAdmin admin,
            IsapiClient isapi, AppDbContext db, ILoggerFactory logs, CancellationToken ct) =>
        {
            var s = ValidateIp(req);
            var c = await access.ConnectAsync(http.User, id, ct);
            var before = (await admin.GetNetworkSettingsAsync(c, ct)).Ip
                ?? throw new AppException("The device didn't report its current network settings");
            var moving = s.Addressing == "static" && s.IpAddress != before.IpAddress;

            bool reboot;
            try
            {
                reboot = await admin.SetIpAsync(c, s, ct);
            }
            catch (IsapiException) when (moving)
            {
                // The device may switch address before it manages to answer; look for it at the new one.
                if (!await ReachableAsync(isapi, c with { Host = s.IpAddress }, ct)) throw;
                reboot = false;
            }

            string? note = null;
            if (s.Addressing == "dhcp" && before.Addressing != "dhcp")
            {
                note = "The NVR now gets its address from your router. If the address changes, update the host on the Devices page.";
            }
            else if (moving && reboot)
            {
                note = $"Reboot the NVR to apply the change, then set this device's host to {s.IpAddress} on the Devices page.";
            }
            else if (moving && await db.Devices.FindAsync([id], ct) is { } device && device.Host == before.IpAddress)
            {
                device.Host = s.IpAddress;
                await db.SaveChangesAsync(ct);
                access.ForgetChannels(id);
                note = $"Camera Viewer now connects to {s.IpAddress}.";
            }
            Audit(logs, http, c, $"changed network settings to {s.Addressing} {s.IpAddress}/{s.SubnetMask} gw {s.Gateway} dns {s.PrimaryDns},{s.SecondaryDns}");
            return new ChangeResult(reboot, note);
        });

        group.MapPut("/network/ports", async (int id, PortSettings req, HttpContext http, DeviceAccess access, NvrAdmin admin,
            IsapiClient isapi, AppDbContext db, ILoggerFactory logs, CancellationToken ct) =>
        {
            int[] all = [req.HttpPort, req.RtspPort, req.ServerPort, .. req.HttpsEnabled ? new[] { req.HttpsPort } : []];
            if (all.Any(p => p is < 1 or > 65535)) throw new AppException("Ports must be between 1 and 65535");
            if (req.ServerPort < 2000) throw new AppException("The server (SDK) port must be 2000 or higher");
            if (all.Distinct().Count() != all.Length) throw new AppException("Each service needs its own port");

            var c = await access.ConnectAsync(http.User, id, ct);
            var before = (await admin.GetNetworkSettingsAsync(c, ct)).Ports
                ?? throw new AppException("The device didn't report its current ports");
            var httpMoving = req.HttpPort != before.HttpPort;

            bool reboot;
            try
            {
                reboot = await admin.SetPortsAsync(c, req, ct);
            }
            catch (IsapiException) when (httpMoving)
            {
                if (!await ReachableAsync(isapi, c with { HttpPort = req.HttpPort }, ct)) throw;
                reboot = false;
            }

            string? note = null;
            var appPortsChanged = httpMoving || req.RtspPort != before.RtspPort;
            if (appPortsChanged && reboot)
            {
                note = $"Reboot the NVR to apply the change, then set HTTP {req.HttpPort} / RTSP {req.RtspPort} for this device on the Devices page.";
            }
            else if (appPortsChanged && await db.Devices.FindAsync([id], ct) is { } device)
            {
                if (device.HttpPort == before.HttpPort) device.HttpPort = req.HttpPort;
                if (device.RtspPort == before.RtspPort) device.RtspPort = req.RtspPort;
                await db.SaveChangesAsync(ct);
                note = "Camera Viewer's connection settings for this device were updated to match.";
            }
            Audit(logs, http, c, $"changed ports to http {req.HttpPort}, https {(req.HttpsEnabled ? req.HttpsPort : "off")}, rtsp {req.RtspPort}, server {req.ServerPort}");
            return new ChangeResult(reboot, note);
        });

        group.MapPut("/network/remote", async (int id, RemoteAccessRequest req, HttpContext http, DeviceAccess access, NvrAdmin admin,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var c = await access.ConnectAsync(http.User, id, ct);
            var reboot = await admin.SetRemoteAccessAsync(c, req.UpnpEnabled, req.SshEnabled, ct);
            Audit(logs, http, c, $"set remote access: UPnP {req.UpnpEnabled?.ToString() ?? "unchanged"}, SSH {req.SshEnabled?.ToString() ?? "unchanged"}");
            return new ChangeResult(reboot, null);
        });

        group.MapPut("/network/ddns", async (int id, DdnsUpdate req, HttpContext http, DeviceAccess access, NvrAdmin admin,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var server = (req.Server ?? "").Trim();
            var domain = (req.Domain ?? "").Trim();
            var user = (req.UserName ?? "").Trim();
            if (req.Enabled && (server.Length == 0 || domain.Length == 0))
                throw new AppException("DDNS needs a server address and a domain name");
            if (server.Length > 128 || domain.Length > 128 || user.Length > 64 || (req.Password?.Length ?? 0) > 64)
                throw new AppException("DDNS server and domain are limited to 128 characters, user name and password to 64");
            if (server.Length > 0 && Uri.CheckHostName(server) == UriHostNameType.Unknown)
                throw new AppException("The DDNS server must be a hostname or IP address");
            if (req.Port is < 0 or > 65535) throw new AppException("Port must be between 0 and 65535 (0 = provider default)");

            var c = await access.ConnectAsync(http.User, id, ct);
            var reboot = await admin.SetDdnsAsync(c, req with { Server = server, Domain = domain, UserName = user }, ct);
            Audit(logs, http, c, $"set DDNS {(req.Enabled ? "on" : "off")}: {req.Provider} {server} {domain} user {user}" +
                (string.IsNullOrEmpty(req.Password) ? "" : " (new password)"));
            return new ChangeResult(reboot, null);
        });

        group.MapPut("/network/cloud", async (int id, CloudUpdate req, HttpContext http, DeviceAccess access, NvrAdmin admin,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var server = (req.Server ?? "").Trim();
            if (server.Length == 0 || server.Length > 128 || Uri.CheckHostName(server) == UriHostNameType.Unknown)
                throw new AppException("The cloud server must be a hostname or IP address");
            if (!string.IsNullOrEmpty(req.VerificationCode) && !CloudCode().IsMatch(req.VerificationCode))
                throw new AppException("The verification code must be 6-12 letters or digits");

            var c = await access.ConnectAsync(http.User, id, ct);
            var reboot = await admin.SetCloudAsync(c, req with { Server = server }, ct);
            Audit(logs, http, c, $"set cloud service {(req.Enabled ? "on" : "off")} via {server}" +
                (string.IsNullOrEmpty(req.VerificationCode) ? "" : " (new verification code)"));
            return new ChangeResult(reboot, req.Enabled
                ? "Cloud access is on. Add the NVR in the Hik-Connect app using its serial number and the verification code."
                : null);
        });

        // ----- system -----

        group.MapGet("/system", async (int id, HttpContext http, DeviceAccess access, NvrAdmin admin, CancellationToken ct) =>
            await admin.GetSystemSettingsAsync(await access.ConnectAsync(http.User, id, ct), ct));

        group.MapPut("/system", async (int id, SystemSettings req, HttpContext http, DeviceAccess access, NvrAdmin admin,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var name = (req.DeviceName ?? "").Trim();
            if (name.Length is < 1 or > 32) throw new AppException("The device name must be 1-32 characters");
            var c = await access.ConnectAsync(http.User, id, ct);
            var reboot = await admin.SetSystemSettingsAsync(c, req with { DeviceName = name }, ct);
            Audit(logs, http, c, $"set device name \"{name}\", overwrite {req.OverwriteWhenFull?.ToString() ?? "unchanged"}");
            return new ChangeResult(reboot, null);
        });

        // ----- logs -----

        // from/to are device-local "yyyy-MM-ddTHH:mm[:ss]"; type is Alarm, Exception, Operation, Information or empty for all.
        group.MapGet("/logs", async (int id, string from, string to, string? type, int? page, HttpContext http,
            DeviceAccess access, NvrAdmin admin, CancellationToken ct) =>
        {
            if (!TryParseLocal(from, out var start) || !TryParseLocal(to, out var end) || end <= start)
                throw new AppException("Give a valid from/to range");
            if (!string.IsNullOrEmpty(type) && !NvrAdmin.LogTypes.Contains(type))
                throw new AppException($"Log type must be one of {string.Join(", ", NvrAdmin.LogTypes)}");

            var c = await access.ConnectAsync(http.User, id, ct);
            return await admin.SearchLogsAsync(c, start, end, string.IsNullOrEmpty(type) ? null : type, Math.Max(0, page ?? 0), 100, ct);
        });

        // ----- device accounts -----

        group.MapGet("/users", async (int id, HttpContext http, DeviceAccess access, NvrAdmin admin, CancellationToken ct) =>
        {
            var c = await access.ConnectAsync(http.User, id, ct);
            var users = await admin.GetUsersAsync(c, ct);
            return new { users, appAccount = c.Username, levels = NvrAdmin.UserLevels };
        });

        group.MapPost("/users", async (int id, CreateNvrUserRequest req, HttpContext http, DeviceAccess access, NvrAdmin admin,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var name = (req.UserName ?? "").Trim();
            if (!DeviceUserName().IsMatch(name))
                throw new AppException("User names must be 1-32 letters, digits, '.', '-' or '_'");
            ValidateDevicePassword(req.Password);
            // Hikvision devices have a single built-in administrator.
            if (req.Level is not ("Operator" or "Viewer"))
                throw new AppException("New device accounts must be Operator or Viewer");

            var c = await access.ConnectAsync(http.User, id, ct);
            await admin.CreateUserAsync(c, name, req.Password, req.Level, ct);
            Audit(logs, http, c, $"created device account \"{name}\" ({req.Level})");
            return Results.NoContent();
        });

        group.MapPut("/users/{uid:int}", async (int id, int uid, UpdateNvrUserRequest req, HttpContext http, DeviceAccess access,
            NvrAdmin admin, AppDbContext db, DeviceSecrets secrets, ILoggerFactory logs, CancellationToken ct) =>
        {
            var c = await access.ConnectAsync(http.User, id, ct);
            var user = (await admin.GetUsersAsync(c, ct)).FirstOrDefault(u => u.Id == uid)
                ?? throw new AppException("Device account not found", StatusCodes.Status404NotFound);

            var password = string.IsNullOrEmpty(req.Password) ? null : req.Password;
            if (password is not null) ValidateDevicePassword(password);
            var level = req.Level == user.Level ? null : req.Level;
            if (level is not null && (user.BuiltIn || level is not ("Operator" or "Viewer")))
                throw new AppException(user.BuiltIn ? "The built-in administrator's level can't be changed" : "Level must be Operator or Viewer");
            if (password is null && level is null) return Results.NoContent();

            await admin.UpdateUserAsync(c, uid, password, level, ct);

            // Keep this app connected if its own device account's password was changed.
            var isAppAccount = string.Equals(user.UserName, c.Username, StringComparison.OrdinalIgnoreCase);
            if (isAppAccount && password is not null && await db.Devices.FindAsync([id], ct) is { } device)
            {
                device.ProtectedPassword = secrets.Protect(password);
                await db.SaveChangesAsync(ct);
            }
            Audit(logs, http, c, $"updated device account \"{user.UserName}\"" +
                $"{(password is null ? "" : " password")}{(level is null ? "" : $" level->{level}")}" +
                $"{(isAppAccount && password is not null ? " (app connection updated)" : "")}");
            return Results.NoContent();
        });

        group.MapDelete("/users/{uid:int}", async (int id, int uid, HttpContext http, DeviceAccess access, NvrAdmin admin,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var c = await access.ConnectAsync(http.User, id, ct);
            var user = (await admin.GetUsersAsync(c, ct)).FirstOrDefault(u => u.Id == uid)
                ?? throw new AppException("Device account not found", StatusCodes.Status404NotFound);
            if (user.BuiltIn) throw new AppException("The built-in administrator can't be deleted");
            if (string.Equals(user.UserName, c.Username, StringComparison.OrdinalIgnoreCase))
                throw new AppException("This is the account Camera Viewer uses to connect; change the device's login on the Devices page first");

            await admin.DeleteUserAsync(c, uid, ct);
            Audit(logs, http, c, $"deleted device account \"{user.UserName}\"");
            return Results.NoContent();
        });
    }

    private static void ValidateDevicePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > MaxDevicePasswordLength)
            throw new AppException($"Device passwords must be 1-{MaxDevicePasswordLength} characters");
    }

    private static IpSettings ValidateIp(IpSettings s)
    {
        if (s.Addressing is not ("static" or "dhcp")) throw new AppException("Addressing must be static or dhcp");
        static uint? V4(string? text) =>
            IPAddress.TryParse(text?.Trim(), out var a) && a.AddressFamily == AddressFamily.InterNetwork && text!.Trim().Count(ch => ch == '.') == 3
                ? BinaryPrimitives.ReadUInt32BigEndian(a.GetAddressBytes())
                : null;

        string Dns(string? text, string what) => string.IsNullOrWhiteSpace(text)
            ? "0.0.0.0"
            : V4(text) is null ? throw new AppException($"{what} is not a valid IPv4 address") : text.Trim();

        var primary = Dns(s.PrimaryDns, "Primary DNS");
        var secondary = Dns(s.SecondaryDns, "Secondary DNS");
        if (s.Addressing == "dhcp") return s with { PrimaryDns = primary, SecondaryDns = secondary };

        var ip = V4(s.IpAddress) ?? throw new AppException("IP address is not a valid IPv4 address");
        var mask = V4(s.SubnetMask) ?? throw new AppException("Subnet mask is not valid");
        var gw = V4(s.Gateway) ?? throw new AppException("Gateway is not a valid IPv4 address");
        // A mask must be a run of 1 bits followed by 0 bits.
        if (mask == 0 || ((~mask + 1) & ~mask) != 0) throw new AppException("Subnet mask is not valid");
        if ((ip & mask) != (gw & mask)) throw new AppException("The gateway must be on the same subnet as the IP address");
        if ((ip & ~mask) == 0 || (ip & ~mask) == ~mask) throw new AppException("That IP address is the subnet's network or broadcast address");
        return new IpSettings("static", s.IpAddress.Trim(), s.SubnetMask.Trim(), s.Gateway.Trim(), primary, secondary);
    }

    private static async Task<bool> ReachableAsync(IsapiClient isapi, DeviceConnection candidate, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await isapi.GetDeviceInfoAsync(candidate, ct);
                return true;
            }
            catch (IsapiException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        return false;
    }

    private static bool TryParseLocal(string? s, out DateTime value) =>
        DateTime.TryParseExact(s, ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static void Audit(ILoggerFactory logs, HttpContext http, DeviceConnection c, string action) =>
        logs.CreateLogger("CameraViewer.Audit").LogWarning("{User} {Action} on {Device} ({Host})",
            http.User.Identity?.Name, action, c.Name, c.Host);

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,32}$")]
    private static partial Regex DeviceUserName();

    [GeneratedRegex(@"^[A-Za-z0-9]{6,12}$")]
    private static partial Regex CloudCode();
}
