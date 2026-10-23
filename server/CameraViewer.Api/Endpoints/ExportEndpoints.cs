using CameraViewer.Api.Auth;
using CameraViewer.Api.Data;
using CameraViewer.Api.Models;
using CameraViewer.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CameraViewer.Api.Endpoints;

/// <summary>Start/End are device-local times. Codec is "h264" (default) or "h265".</summary>
public record CreateExportRequest(int DeviceId, int Channel, DateTime Start, DateTime End, string? Codec);

public static class ExportEndpoints
{
    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/exports");

        group.MapGet("/", async (HttpContext http, AppDbContext db) =>
        {
            var query = db.ExportJobs.AsQueryable();
            if (!http.User.IsAdmin())
            {
                var uid = http.User.UserId();
                query = query.Where(j => j.UserId == uid);
            }
            return await query.OrderByDescending(j => j.CreatedAt).Take(200).ToListAsync();
        });

        group.MapPost("/", async (CreateExportRequest req, HttpContext http, AppDbContext db, DeviceAccess access,
            ExportService exports, CancellationToken ct) =>
        {
            var codec = (req.Codec ?? "h264").ToLowerInvariant();
            if (codec is not ("h264" or "h265"))
                return Results.BadRequest(new { error = "Codec must be h264 or h265" });
            var duration = req.End - req.Start;
            if (duration <= TimeSpan.Zero)
                return Results.BadRequest(new { error = "End time must be after start time" });
            if (duration > TimeSpan.FromMinutes(exports.MaxMinutes))
                return Results.BadRequest(new { error = $"Exports are limited to {exports.MaxMinutes} minutes" });

            var conn = await access.ConnectAsync(http.User, req.DeviceId, ct);
            string channelName;
            try
            {
                channelName = (await access.GetChannelsAsync(conn, ct)).FirstOrDefault(c => c.Id == req.Channel)?.Name
                    ?? $"Channel {req.Channel}";
            }
            catch (IsapiException)
            {
                channelName = $"Channel {req.Channel}";
            }

            var job = new ExportJob
            {
                UserId = http.User.UserId(),
                Username = http.User.Identity!.Name!,
                DeviceId = conn.Id,
                DeviceName = conn.Name,
                Channel = req.Channel,
                ChannelName = channelName,
                Start = DateTime.SpecifyKind(req.Start, DateTimeKind.Unspecified),
                End = DateTime.SpecifyKind(req.End, DateTimeKind.Unspecified),
                Codec = codec,
            };
            db.ExportJobs.Add(job);
            await db.SaveChangesAsync(ct);
            exports.Enqueue(job.Id);
            return Results.Ok(job);
        });

        group.MapGet("/{id}/download", async (string id, HttpContext http, AppDbContext db, ExportService exports) =>
        {
            var job = await FindOwnedAsync(db, http, id);
            if (job is not { Status: ExportStatus.Completed }) return Results.NotFound();
            var path = exports.FilePath(job.Id);
            if (!File.Exists(path)) return Results.NotFound();
            return Results.File(path, "video/mp4", DownloadName(job), enableRangeProcessing: true);
        });

        group.MapDelete("/{id}", async (string id, HttpContext http, AppDbContext db, ExportService exports) =>
        {
            var job = await FindOwnedAsync(db, http, id);
            if (job is null) return Results.NotFound();
            if (exports.TryCancelActive(id))
                return Results.Accepted();

            ExportService.TryDelete(exports.FilePath(id));
            db.ExportJobs.Remove(job);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static async Task<ExportJob?> FindOwnedAsync(AppDbContext db, HttpContext http, string id)
    {
        var job = await db.ExportJobs.FindAsync(id);
        return job is not null && (http.User.IsAdmin() || job.UserId == http.User.UserId()) ? job : null;
    }

    private static string DownloadName(ExportJob job)
    {
        var raw = $"{job.DeviceName}_{job.ChannelName}_{job.Start:yyyyMMdd_HHmmss}-{job.End:HHmmss}.mp4";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(raw.Select(ch => invalid.Contains(ch) || ch == ' ' ? '_' : ch).ToArray());
    }
}
