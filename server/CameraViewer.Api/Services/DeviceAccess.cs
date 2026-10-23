using System.Security.Claims;
using CameraViewer.Api.Auth;
using CameraViewer.Api.Data;
using CameraViewer.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CameraViewer.Api.Services;

/// <summary>Admins see every device; viewers see only the devices assigned to them.</summary>
public class DeviceAccess(AppDbContext db, DeviceSecrets secrets, IsapiClient isapi, IMemoryCache cache)
{
    public IQueryable<Device> Visible(ClaimsPrincipal user)
    {
        if (user.IsAdmin()) return db.Devices;
        var uid = user.UserId();
        return db.Devices.Where(d => db.UserDevices.Any(ud => ud.UserId == uid && ud.DeviceId == d.Id));
    }

    public async Task<bool> CanAccessAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct) =>
        await Visible(user).AnyAsync(d => d.Id == deviceId, ct);

    /// <summary>Returns the device connection, or throws a 404 if it doesn't exist or isn't visible to the user.</summary>
    public async Task<DeviceConnection> ConnectAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct)
    {
        var device = await Visible(user).FirstOrDefaultAsync(d => d.Id == deviceId, ct)
            ?? throw new AppException("Device not found", StatusCodes.Status404NotFound);
        return secrets.Connect(device);
    }

    /// <summary>Channel lists are cached briefly since every grid tile and page load needs them.</summary>
    public async Task<List<ChannelInfo>> GetChannelsAsync(DeviceConnection c, CancellationToken ct) =>
        (await cache.GetOrCreateAsync(ChannelsKey(c.Id), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return isapi.GetChannelsAsync(c, ct);
        }))!;

    public void ForgetChannels(int deviceId) => cache.Remove(ChannelsKey(deviceId));

    private static string ChannelsKey(int deviceId) => $"channels:{deviceId}";
}
