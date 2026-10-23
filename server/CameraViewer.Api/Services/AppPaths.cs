using System.Security.AccessControl;
using System.Security.Principal;

namespace CameraViewer.Api.Services;

/// <summary>Resolves where the app keeps its database, keys, exports and transient HLS output.</summary>
public class AppPaths
{
    public AppPaths(IConfiguration config, IWebHostEnvironment env)
    {
        var data = config["Storage:DataPath"];
        DataDir = Path.GetFullPath(string.IsNullOrWhiteSpace(data) ? "App_Data" : data, env.ContentRootPath);
        KeysDir = Path.Combine(DataDir, "keys");
        ExportsDir = Path.Combine(DataDir, "exports");
        // Per data directory, so two instances never clean up each other's live streams.
        var instance = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(DataDir.ToLowerInvariant())))[..12];
        HlsDir = Path.Combine(Path.GetTempPath(), "cameraviewer-hls", instance);
        DatabasePath = Path.Combine(DataDir, "cameraviewer.db");

        foreach (var dir in new[] { DataDir, KeysDir, ExportsDir, HlsDir }) EnsurePrivateDirectory(dir);
    }

    /// <summary>
    /// On Linux/macOS these folders hold the encryption keys (stored unencrypted there, unlike DPAPI on Windows)
    /// and camera footage, so restrict them to the account running the server.
    /// </summary>
    public static void EnsurePrivateDirectory(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
            return;
        }
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// For secrets such as the Tailscale node key. On Windows this also replaces the inherited ACL with one that
    /// grants only the current user, SYSTEM and Administrators. Only the DACL is changed (which the folder's owner
    /// may always do), never the owner, so it works even where the user lacks "take ownership" rights.
    /// </summary>
    public static void EnsureOwnerOnlyDirectory(string dir)
    {
        EnsurePrivateDirectory(dir);
        if (!OperatingSystem.IsWindows()) return;

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        SecurityIdentifier[] allowed =
        [
            WindowsIdentity.GetCurrent().User!,
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        ];
        foreach (var sid in allowed)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(security);
    }

    public string DataDir { get; }
    public string KeysDir { get; }
    public string ExportsDir { get; }
    public string HlsDir { get; }
    public string DatabasePath { get; }
}
