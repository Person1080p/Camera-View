using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using CameraViewer.Api.Data;
using CameraViewer.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CameraViewer.Api.Services;

/// <summary>
/// Turns recording clips into downloadable MP4 files. The NVR only serves recordings at 1x speed,
/// so an export takes roughly as long as the clip; jobs therefore run in the background, each
/// waiting for a free playback slot on its device.
/// </summary>
public class ExportService(
    IServiceScopeFactory scopes, DeviceSecrets secrets, FfmpegLocator ffmpeg, PlaybackSlots slots, AppPaths paths,
    IConfiguration config, ILogger<ExportService> log) : BackgroundService
{
    private const int MaxBusyRetries = 20;
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(15);

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public int MaxMinutes => config.GetValue("Exports:MaxMinutes", 120);

    public string FilePath(string id) => Path.Combine(paths.ExportsDir, id + ".mp4");

    public void Enqueue(string id) => _queue.Writer.TryWrite(id);

    /// <summary>Cancels a job that a worker has picked up; the worker then deletes its row and partial file.</summary>
    public bool TryCancelActive(string id)
    {
        if (!_active.TryGetValue(id, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        var workers = Math.Max(1, config.GetValue("Exports:MaxConcurrent", 2));
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => WorkAsync(stoppingToken)));
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var interrupted = await db.ExportJobs.Where(j => j.Status == ExportStatus.Running).ToListAsync(ct);
        foreach (var job in interrupted)
        {
            job.Status = ExportStatus.Failed;
            job.Error = "Interrupted by a server restart";
            TryDelete(FilePath(job.Id) + ".part");
        }
        await db.SaveChangesAsync(ct);

        var queued = await db.ExportJobs.Where(j => j.Status == ExportStatus.Queued)
            .OrderBy(j => j.CreatedAt).Select(j => j.Id).ToListAsync(ct);
        queued.ForEach(Enqueue);
    }

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await RunAsync(id, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "Export {Id} crashed", id);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(string id, CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.ExportJobs.FindAsync([id], stoppingToken);
        if (job is null || job.Status != ExportStatus.Queued) return;

        var device = await db.Devices.FindAsync([job.DeviceId], stoppingToken);
        if (device is null)
        {
            await FinishAsync(db, job, ExportStatus.Failed, "The device was removed");
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _active[id] = cts;
        var conn = secrets.Connect(device);
        var part = FilePath(id) + ".part";
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                await AcquireSlotAsync(db, job, conn, cts.Token);
                (int? exitCode, string error) result;
                try
                {
                    job.Status = ExportStatus.Running;
                    job.Message = null;
                    await db.SaveChangesAsync(CancellationToken.None);
                    result = await TranscodeAsync(db, job, conn, part, cts.Token);
                }
                finally
                {
                    slots.Release(conn.Id, id);
                }

                var size = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (result.exitCode == 0 && size >= 1024)
                {
                    File.Move(part, FilePath(id), overwrite: true);
                    job.FileSize = size;
                    job.Progress = 1;
                    await FinishAsync(db, job, ExportStatus.Completed, null);
                    log.LogInformation("Export {Id} finished ({Size:N0} bytes)", id, size);
                    return;
                }

                TryDelete(part);
                // 453 means something outside this app (e.g. the iVMS client) is using the NVR's playback.
                if (result.error.Contains("453") && attempt < MaxBusyRetries)
                {
                    job.Status = ExportStatus.Queued;
                    job.Progress = 0;
                    job.Message = "The device is busy serving another playback client; retrying shortly";
                    await db.SaveChangesAsync(CancellationToken.None);
                    await Task.Delay(BusyRetryDelay, cts.Token);
                    continue;
                }

                await FinishAsync(db, job, ExportStatus.Failed,
                    string.IsNullOrWhiteSpace(result.error) ? "No recording was found in the selected range" : result.error);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            if (stoppingToken.IsCancellationRequested)
            {
                await FinishAsync(db, job, ExportStatus.Failed, "Interrupted by a server shutdown");
            }
            else
            {
                db.ExportJobs.Remove(job);
                await SaveIgnoringConcurrencyAsync(db);
            }
        }
        catch (Exception ex)
        {
            TryDelete(part);
            await FinishAsync(db, job, ExportStatus.Failed, ex.Message);
        }
        finally
        {
            _active.TryRemove(id, out _);
        }
    }

    private async Task AcquireSlotAsync(AppDbContext db, ExportJob job, DeviceConnection conn, CancellationToken ct)
    {
        var holder = new SlotHolder(SlotKind.Export, job.Id, job.UserId, job.Username);
        while (!slots.TryAcquire(conn.Id, conn.PlaybackLimit, holder, out var blocker))
        {
            var message = $"Waiting for {blocker!.Describe()} to finish (the device allows {conn.PlaybackLimit} recording stream{(conn.PlaybackLimit == 1 ? "" : "s")} at a time)";
            if (job.Message != message)
            {
                job.Message = message;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task<(int? ExitCode, string Error)> TranscodeAsync(AppDbContext db, ExportJob job, DeviceConnection conn, string part, CancellationToken ct)
    {
        var seconds = (job.End - job.Start).TotalSeconds;
        string[] codec = job.Codec == "h265"
            ? ["-c:v", "copy", "-tag:v", "hvc1"]
            : ["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-vf", "scale='min(1920,iw)':-2"];

        using var proc = FfmpegProcess.Start(ffmpeg.Require(),
        [
            "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
            "-rtsp_transport", "tcp", "-timeout", "10000000",
            "-i", RtspUrls.Playback(conn, job.Channel, job.Start, job.End),
            "-t", seconds.ToString(CultureInfo.InvariantCulture),
            "-map", "0:v:0", "-an", .. codec,
            "-movflags", "+faststart", "-f", "mp4", "-y", part,
        ]);

        double progress = 0;
        proc.OutputLine += line =>
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan("out_time_us=".Length), out var us))
                progress = Math.Clamp(us / 1_000_000d / seconds, 0, 1);
        };

        try
        {
            while (!await proc.WaitForExitAsync(TimeSpan.FromSeconds(2), ct))
            {
                job.Progress = Math.Round(progress, 3);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            proc.Kill();
            await proc.WaitForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            throw;
        }
        return (proc.ExitCode, proc.ErrorTail);
    }

    private static async Task FinishAsync(AppDbContext db, ExportJob job, ExportStatus status, string? error)
    {
        job.Status = status;
        job.Error = error;
        job.Message = null;
        job.CompletedAt = DateTime.UtcNow;
        await SaveIgnoringConcurrencyAsync(db);
    }

    // The owner may delete a job row at any moment; losing that race is harmless.
    private static async Task SaveIgnoringConcurrencyAsync(AppDbContext db)
    {
        try { await db.SaveChangesAsync(CancellationToken.None); }
        catch (DbUpdateConcurrencyException) { }
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
