namespace CameraViewer.Api.Models;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";

    public static bool IsValid(string role) => role is Admin or Viewer;
}

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;
    public bool MustChangePassword { get; set; }

    /// <summary>Rotated whenever credentials or permissions change; invalidates existing login cookies.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<UserDevice> DeviceAccess { get; set; } = [];
}

/// <summary>An NVR, DVR or standalone IP camera that speaks Hikvision ISAPI + RTSP.</summary>
public class Device
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int HttpPort { get; set; } = 80;
    public int RtspPort { get; set; } = 554;
    public string Username { get; set; } = "";

    /// <summary>How many recording streams (playback + exports) the device serves at once. Many NVRs allow only one.</summary>
    public int PlaybackLimit { get; set; } = 1;

    /// <summary>Reach the device through the app's embedded Tailscale node (e.g. an NVR behind a subnet router).</summary>
    public bool UseTailscale { get; set; }

    /// <summary>Device password encrypted with ASP.NET Data Protection.</summary>
    public string ProtectedPassword { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class UserDevice
{
    public int UserId { get; set; }
    public User? User { get; set; }
    public int DeviceId { get; set; }
    public Device? Device { get; set; }
}

public enum ExportStatus { Queued, Running, Completed, Failed, Cancelled }

/// <summary>
/// A recording clip being converted to MP4. Device and channel names are copied in so that
/// finished exports stay listed and downloadable after a device is removed.
/// </summary>
public class ExportJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int UserId { get; set; }
    public string Username { get; set; } = "";
    public int DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public int Channel { get; set; }
    public string ChannelName { get; set; } = "";

    /// <summary>Device-local wall-clock time (the NVR has no notion of UTC in its recording index).</summary>
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

    /// <summary>"h264" (re-encoded, plays everywhere) or "h265" (original stream, no quality loss).</summary>
    public string Codec { get; set; } = "h264";

    public ExportStatus Status { get; set; } = ExportStatus.Queued;
    public double Progress { get; set; }
    public long? FileSize { get; set; }
    public string? Error { get; set; }

    /// <summary>Informational status while queued, e.g. waiting for the device's playback slot.</summary>
    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
