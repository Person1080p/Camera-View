using CameraViewer.Api.Models;
using Microsoft.AspNetCore.DataProtection;

namespace CameraViewer.Api.Services;

/// <summary>Everything needed to talk to a device, with the password decrypted.</summary>
public sealed record DeviceConnection(int Id, string Name, string Host, int HttpPort, int RtspPort, string Username, string Password, int PlaybackLimit = 1);

public class DeviceSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("CameraViewer.DevicePasswords");

    public string Protect(string password) => _protector.Protect(password);

    public DeviceConnection Connect(Device d) =>
        new(d.Id, d.Name, d.Host, d.HttpPort, d.RtspPort, d.Username, _protector.Unprotect(d.ProtectedPassword), d.PlaybackLimit);
}
