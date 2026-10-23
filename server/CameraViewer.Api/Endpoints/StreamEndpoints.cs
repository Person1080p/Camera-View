using System.Text.RegularExpressions;
using CameraViewer.Api.Auth;
using CameraViewer.Api.Services;

namespace CameraViewer.Api.Endpoints;

public record LiveStreamRequest(int DeviceId, int Channel, string? Quality);

/// <summary>Start is device-local time, e.g. "2026-10-23T08:30:00".</summary>
public record PlaybackStreamRequest(int DeviceId, int Channel, DateTime Start, string? Quality);

public record StreamDto(string Id, string Playlist);

public static partial class StreamEndpoints
{
    public static void MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/streams");

        group.MapPost("/live", async (LiveStreamRequest req, HttpContext http, DeviceAccess access, StreamManager streams, CancellationToken ct) =>
        {
            var conn = await access.ConnectAsync(http.User, req.DeviceId, ct);
            var session = await streams.StartLiveAsync(conn, req.Channel, req.Quality ?? "sd", http.User.UserId(), ct);
            return new StreamDto(session.Id, PlaylistUrl(session));
        });

        group.MapPost("/playback", async (PlaybackStreamRequest req, HttpContext http, DeviceAccess access, StreamManager streams, CancellationToken ct) =>
        {
            var conn = await access.ConnectAsync(http.User, req.DeviceId, ct);
            var start = DateTime.SpecifyKind(req.Start, DateTimeKind.Unspecified);
            var session = await streams.StartPlaybackAsync(conn, req.Channel, start, req.Quality ?? "sd",
                http.User.UserId(), http.User.Identity!.Name!, ct);
            return new StreamDto(session.Id, PlaylistUrl(session));
        });

        group.MapDelete("/{id}", (string id, HttpContext http, StreamManager streams) =>
        {
            // Live sessions are shared and are reaped when nobody is watching; only playback is stopped eagerly.
            var session = streams.Get(id);
            if (session is { Kind: StreamKind.Playback } && session.Viewers.ContainsKey(http.User.UserId()))
                streams.Stop(id);
            return Results.NoContent();
        });

        group.MapGet("/{id}/{file}", async (string id, string file, HttpContext http, StreamManager streams, DeviceAccess access, CancellationToken ct) =>
        {
            var session = streams.Get(id);
            if (session is null || !HlsFileName().IsMatch(file)) return Results.NotFound();

            // Joining someone else's shared live session is fine as long as you can see that device.
            var uid = http.User.UserId();
            if (!session.Viewers.ContainsKey(uid))
            {
                if (session.Kind != StreamKind.Live || !await access.CanAccessAsync(http.User, session.DeviceId, ct))
                    return Results.NotFound();
                session.Viewers.TryAdd(uid, 0);
            }

            session.Touch();
            var path = Path.Combine(session.Directory, file);
            byte[] bytes;
            try
            {
                // ffmpeg replaces the playlist and deletes old segments while we read, so allow that.
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
                bytes = new byte[fs.Length];
                await fs.ReadExactlyAsync(bytes, ct);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "no-cache";
            return Results.Bytes(bytes, file.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t");
        });
    }

    private static string PlaylistUrl(StreamSession session) => $"/api/streams/{session.Id}/index.m3u8";

    [GeneratedRegex(@"^(index\.m3u8|seg_\d{5}\.ts)$")]
    private static partial Regex HlsFileName();
}
