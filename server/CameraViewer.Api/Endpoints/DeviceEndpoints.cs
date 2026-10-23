using System.Globalization;
using CameraViewer.Api.Auth;
using CameraViewer.Api.Data;
using CameraViewer.Api.Models;
using CameraViewer.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CameraViewer.Api.Endpoints;

/// <summary>Connection details are only included for admins.</summary>
public record DeviceDto(int Id, string Name, int PlaybackLimit, string? Host, int? HttpPort, int? RtspPort, string? Username)
{
    public static DeviceDto For(Device d, bool admin) => admin
        ? new(d.Id, d.Name, d.PlaybackLimit, d.Host, d.HttpPort, d.RtspPort, d.Username)
        : new(d.Id, d.Name, d.PlaybackLimit, null, null, null, null);
}

/// <summary>Password may be left empty on update to keep the stored one.</summary>
public record SaveDeviceRequest(string Name, string Host, int HttpPort, int RtspPort, string Username, string? Password, int? PlaybackLimit);

/// <summary>Id lets the edit form test with the stored password when the password field is left blank.</summary>
public record TestDeviceRequest(int? Id, string Host, int HttpPort, string Username, string? Password);

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices");

        group.MapGet("/", async (HttpContext http, DeviceAccess access) =>
        {
            var admin = http.User.IsAdmin();
            return (await access.Visible(http.User).OrderBy(d => d.Name).ToListAsync())
                .Select(d => DeviceDto.For(d, admin));
        });

        group.MapPost("/", async (SaveDeviceRequest req, AppDbContext db, DeviceSecrets secrets) =>
        {
            if (Validate(req, requirePassword: true) is { } error) return Results.BadRequest(new { error });
            var device = new Device { ProtectedPassword = secrets.Protect(req.Password!) };
            Apply(device, req);
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            return Results.Ok(DeviceDto.For(device, admin: true));
        }).RequireAuthorization("Admin");

        group.MapPut("/{id:int}", async (int id, SaveDeviceRequest req, AppDbContext db, DeviceSecrets secrets, DeviceAccess access) =>
        {
            var device = await db.Devices.FindAsync(id);
            if (device is null) return Results.NotFound(new { error = "Device not found" });
            if (Validate(req, requirePassword: false) is { } error) return Results.BadRequest(new { error });

            Apply(device, req);
            if (!string.IsNullOrEmpty(req.Password)) device.ProtectedPassword = secrets.Protect(req.Password);
            await db.SaveChangesAsync();
            access.ForgetChannels(id);
            return Results.Ok(DeviceDto.For(device, admin: true));
        }).RequireAuthorization("Admin");

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, DeviceAccess access) =>
        {
            var device = await db.Devices.FindAsync(id);
            if (device is null) return Results.NotFound(new { error = "Device not found" });
            db.Devices.Remove(device);
            await db.SaveChangesAsync();
            access.ForgetChannels(id);
            return Results.NoContent();
        }).RequireAuthorization("Admin");

        group.MapPost("/test", async (TestDeviceRequest req, AppDbContext db, DeviceSecrets secrets, IsapiClient isapi, CancellationToken ct) =>
        {
            var password = req.Password;
            if (string.IsNullOrEmpty(password) && req.Id is { } existingId)
            {
                var existing = await db.Devices.FindAsync([existingId], ct);
                if (existing is not null) password = secrets.Connect(existing).Password;
            }
            var conn = new DeviceConnection(0, "", req.Host.Trim(), req.HttpPort, 554, req.Username, password ?? "");
            var info = await isapi.GetDeviceInfoAsync(conn, ct);
            var channels = await isapi.GetChannelsAsync(conn, ct);
            return Results.Ok(new { info, channelCount = channels.Count });
        }).RequireAuthorization("Admin");

        group.MapGet("/{id:int}/info", async (int id, HttpContext http, DeviceAccess access, IsapiClient isapi, CancellationToken ct) =>
            Results.Ok(await isapi.GetDeviceInfoAsync(await access.ConnectAsync(http.User, id, ct), ct)));

        group.MapGet("/{id:int}/channels", async (int id, HttpContext http, DeviceAccess access, CancellationToken ct) =>
            Results.Ok(await access.GetChannelsAsync(await access.ConnectAsync(http.User, id, ct), ct)));

        group.MapGet("/{id:int}/channels/{channel:int}/snapshot", async (int id, int channel, bool? sub, HttpContext http,
            DeviceAccess access, IsapiClient isapi, CancellationToken ct) =>
        {
            var conn = await access.ConnectAsync(http.User, id, ct);
            var (bytes, type) = await isapi.GetSnapshotAsync(conn, channel, sub ?? false, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(bytes, type);
        });

        // date is yyyy-MM-dd in device-local time; returns merged recorded spans clipped to that day.
        group.MapGet("/{id:int}/channels/{channel:int}/recordings", async (int id, int channel, string date, HttpContext http,
            DeviceAccess access, IsapiClient isapi, CancellationToken ct) =>
        {
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return Results.BadRequest(new { error = "date must be yyyy-MM-dd" });

            var conn = await access.ConnectAsync(http.User, id, ct);
            var dayEnd = day.AddDays(1);
            var segments = (await isapi.SearchRecordingsAsync(conn, channel, day, dayEnd.AddSeconds(-1), ct))
                .Select(s => new RecordingSegment(s.Start < day ? day : s.Start, s.End > dayEnd ? dayEnd : s.End))
                .Where(s => s.End > s.Start)
                .ToList();
            return Results.Ok(new { date, segments });
        });
    }

    private static string? Validate(SaveDeviceRequest req, bool requirePassword)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return "Name is required";
        if (string.IsNullOrWhiteSpace(req.Host) || Uri.CheckHostName(req.Host.Trim()) == UriHostNameType.Unknown)
            return "Host must be an IP address or hostname";
        if (req.HttpPort is < 1 or > 65535 || req.RtspPort is < 1 or > 65535) return "Ports must be between 1 and 65535";
        if (string.IsNullOrWhiteSpace(req.Username)) return "Username is required";
        if (req.PlaybackLimit is < 1 or > 16) return "Playback limit must be between 1 and 16";
        if (requirePassword && string.IsNullOrEmpty(req.Password)) return "Password is required";
        return null;
    }

    private static void Apply(Device d, SaveDeviceRequest req)
    {
        d.Name = req.Name.Trim();
        d.Host = req.Host.Trim();
        d.HttpPort = req.HttpPort;
        d.RtspPort = req.RtspPort;
        d.Username = req.Username.Trim();
        d.PlaybackLimit = req.PlaybackLimit ?? d.PlaybackLimit;
    }
}
