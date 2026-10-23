using System.Collections.Concurrent;
using System.Globalization;

namespace CameraViewer.Api.Services;

public enum StreamKind { Live, Playback }

/// <summary>
/// One ffmpeg process converting an RTSP source into a rolling HLS playlist in its own directory.
/// </summary>
public sealed class StreamSession(string id, StreamKind kind, string? shareKey, int deviceId, int ownerId, string directory, TimeSpan idleTimeout)
{
    private long _lastAccessTicks = DateTime.UtcNow.Ticks;

    public string Id { get; } = id;
    public StreamKind Kind { get; } = kind;
    public string? ShareKey { get; } = shareKey;
    public int DeviceId { get; } = deviceId;
    public int OwnerId { get; } = ownerId;
    public string Directory { get; } = directory;
    public TimeSpan IdleTimeout { get; } = idleTimeout;
    public FfmpegProcess Process { get; set; } = null!;

    /// <summary>Users who started or joined this session; only they may fetch its files.</summary>
    public ConcurrentDictionary<int, byte> Viewers { get; } = new();

    public string PlaylistPath => Path.Combine(Directory, "index.m3u8");

    public DateTime LastAccessUtc => new(Interlocked.Read(ref _lastAccessTicks), DateTimeKind.Utc);

    public void Touch() => Interlocked.Exchange(ref _lastAccessTicks, DateTime.UtcNow.Ticks);

    /// <summary>ffmpeg only writes the playlist once the first segment is complete.</summary>
    public async Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(PlaylistPath)) return true;
            if (Process.HasExited) return false;
            await Task.Delay(200, ct);
        }
        return File.Exists(PlaylistPath);
    }
}

public class StreamManager(AppPaths paths, FfmpegLocator ffmpeg, PlaybackSlots slots, TailscaleService tailscale,
    IConfiguration config, ILogger<StreamManager> log) : BackgroundService
{
    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();
    private readonly object _startLock = new();

    private TimeSpan LiveIdle => TimeSpan.FromSeconds(config.GetValue("Streaming:LiveIdleSeconds", 30));
    private TimeSpan PlaybackIdle => TimeSpan.FromSeconds(config.GetValue("Streaming:PlaybackIdleSeconds", 45));
    private int MaxSessions => config.GetValue("Streaming:MaxSessions", 40);

    public StreamSession? Get(string id) => _sessions.GetValueOrDefault(id);

    /// <summary>
    /// Live streams of the same channel/quality are shared between viewers, so a busy grid view
    /// costs one RTSP connection per camera no matter how many people are watching.
    /// "sd" relays the camera's H.264 sub stream untouched; "hd" transcodes the main stream to 720p
    /// H.264 because the main stream is often H.265, which most browsers cannot decode.
    /// </summary>
    public async Task<StreamSession> StartLiveAsync(DeviceConnection c, int channel, string quality, int userId, CancellationToken ct)
    {
        var hd = quality == "hd";
        var key = $"live:{c.Id}:{channel}:{(hd ? "hd" : "sd")}";
        StreamSession session;
        lock (_startLock)
        {
            var existing = _sessions.Values.FirstOrDefault(s => s.ShareKey == key);
            if (existing is not null && !existing.Process.HasExited)
            {
                existing.Viewers.TryAdd(userId, 0);
                existing.Touch();
                session = existing;
            }
            else
            {
                if (existing is not null) Remove(existing);
                var input = RtspUrls.Live(tailscale.ForRtsp(c), channel, sub: !hd);
                session = Launch(StreamKind.Live, key, c.Id, userId, LiveIdle, dir =>
                [
                    .. InputArgs, "-fflags", "nobuffer", "-i", input,
                    "-map", "0:v:0", "-an",
                    .. (hd ? H264Args(720, "2500k") : ["-c:v", "copy"]),
                    "-f", "hls", "-hls_time", "1", "-hls_list_size", "6",
                    "-hls_flags", "delete_segments+temp_file+omit_endlist",
                    "-hls_segment_filename", Path.Combine(dir, "seg_%05d.ts"),
                    Path.Combine(dir, "index.m3u8"),
                ]);
            }
        }
        await EnsureReadyAsync(session, TimeSpan.FromSeconds(20), ct);
        return session;
    }

    /// <summary>
    /// Plays a recording from <paramref name="start"/> (device-local time). The NVR streams at 1x,
    /// so the playlist grows in real time; seeking past what has arrived means starting a new session.
    /// A user's previous playback on the same device is replaced, since the NVR's slots are scarce.
    /// </summary>
    public async Task<StreamSession> StartPlaybackAsync(DeviceConnection c, int channel, DateTime start, string quality,
        int userId, string username, CancellationToken ct)
    {
        var hours = config.GetValue("Streaming:PlaybackWindowHours", 3);
        var input = RtspUrls.Playback(tailscale.ForRtsp(c), channel, start, start.AddHours(hours));
        var (height, bitrate) = quality == "hd" ? (720, "2500k") : (480, "1200k");

        var replaced = false;
        lock (_startLock)
        {
            foreach (var old in _sessions.Values.Where(s => s.Kind == StreamKind.Playback && s.DeviceId == c.Id && s.OwnerId == userId))
            {
                Remove(old);
                replaced = true;
            }
        }

        // The NVR can take a moment to notice a closed session, so a 453 right after replacing one is retried.
        for (var attempt = 1; ; attempt++)
        {
            StreamSession session;
            lock (_startLock)
            {
                var id = Guid.NewGuid().ToString("N");
                if (!slots.TryAcquire(c.Id, c.PlaybackLimit, new SlotHolder(SlotKind.Playback, id, userId, username), out var blocker))
                    throw new AppException(
                        $"Recording playback on {c.Name} is in use by {blocker!.Describe()}. " +
                        "This device allows only " + (c.PlaybackLimit == 1 ? "one recording stream" : $"{c.PlaybackLimit} recording streams") + " at a time.",
                        StatusCodes.Status409Conflict);

                session = Launch(StreamKind.Playback, null, c.Id, userId, PlaybackIdle, dir =>
                [
                    .. InputArgs, "-i", input,
                    "-map", "0:v:0", "-an",
                    .. H264Args(height, bitrate),
                    "-f", "hls", "-hls_time", "2", "-hls_playlist_type", "event",
                    "-hls_flags", "temp_file",
                    "-hls_segment_filename", Path.Combine(dir, "seg_%05d.ts"),
                    Path.Combine(dir, "index.m3u8"),
                ], id);
            }

            try
            {
                await EnsureReadyAsync(session, TimeSpan.FromSeconds(30), ct);
                return session;
            }
            catch (DeviceBusyException) when (replaced && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    public void Stop(string id)
    {
        if (_sessions.TryGetValue(id, out var s)) Remove(s);
    }

    private async Task EnsureReadyAsync(StreamSession session, TimeSpan timeout, CancellationToken ct)
    {
        if (await session.WaitUntilReadyAsync(timeout, ct)) return;

        var error = session.Process.HasExited ? session.Process.ErrorTail : "";
        Remove(session);
        if (error.Contains("453"))
            throw new DeviceBusyException();
        throw new AppException(string.IsNullOrWhiteSpace(error)
            ? "The device did not send any video (no signal, or no recording at that time)"
            : $"Video stream failed: {error}", StatusCodes.Status502BadGateway);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CleanDirectory(paths.HlsDir);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTime.UtcNow;
                foreach (var s in _sessions.Values)
                {
                    var idle = now - s.LastAccessUtc > s.IdleTimeout;
                    // A dead live relay is dropped straight away so the next request restarts it.
                    var deadLive = s.Kind == StreamKind.Live && s.Process.HasExited;
                    if (idle || deadLive)
                    {
                        if (deadLive && !idle) log.LogWarning("Live stream {Key} exited: {Error}", s.ShareKey, s.Process.ErrorTail);
                        Remove(s);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var s in _sessions.Values) Remove(s);
        }
    }

    private StreamSession Launch(StreamKind kind, string? shareKey, int deviceId, int userId, TimeSpan idle,
        Func<string, List<string>> buildArgs, string? id = null)
    {
        id ??= Guid.NewGuid().ToString("N");
        var session = new StreamSession(id, kind, shareKey, deviceId, userId, Path.Combine(paths.HlsDir, id), idle);
        try
        {
            if (_sessions.Count >= MaxSessions)
                throw new AppException("Too many active streams on the server; close some views and try again.", StatusCodes.Status503ServiceUnavailable);

            Directory.CreateDirectory(session.Directory);
            session.Viewers.TryAdd(userId, 0);
            session.Process = FfmpegProcess.Start(ffmpeg.Require(), buildArgs(session.Directory));
        }
        catch
        {
            if (kind == StreamKind.Playback) slots.Release(deviceId, id);
            throw;
        }
        _sessions[id] = session;
        log.LogInformation("Started {Kind} stream {Id} ({Key})", kind, id, shareKey ?? $"device {deviceId}");
        return session;
    }

    private void Remove(StreamSession s)
    {
        if (!_sessions.TryRemove(s.Id, out _)) return;
        s.Process.Dispose();
        if (s.Kind == StreamKind.Playback) slots.Release(s.DeviceId, s.Id);
        log.LogInformation("Stopped {Kind} stream {Id}", s.Kind, s.Id);
        _ = Task.Run(async () =>
        {
            // Give the killed process a moment to release its file handles.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try { Directory.Delete(s.Directory, recursive: true); return; }
                catch (DirectoryNotFoundException) { return; }
                catch (IOException) { await Task.Delay(1000); }
                catch (UnauthorizedAccessException) { await Task.Delay(1000); }
            }
        });
    }

    private static readonly string[] InputArgs =
        ["-hide_banner", "-loglevel", "error", "-rtsp_transport", "tcp", "-timeout", "10000000"];

    // One keyframe per second keeps HLS segments short and seeking responsive.
    private static string[] H264Args(int height, string bitrate) =>
    [
        "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
        "-vf", $"scale=-2:{height.ToString(CultureInfo.InvariantCulture)}", "-pix_fmt", "yuv420p",
        "-force_key_frames", "expr:gte(t,n_forced*1)", "-sc_threshold", "0",
        "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bitrate,
    ];

    private static void CleanDirectory(string dir)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            try { Directory.Delete(sub, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>The device answered "453 Not Enough Bandwidth": it is at its limit of simultaneous streams.</summary>
public class DeviceBusyException() : AppException(
    "The device refused the stream because it is at its limit of simultaneous streams. " +
    "Close other viewers (including the iVMS app or the NVR's own web page) and try again.",
    StatusCodes.Status503ServiceUnavailable);
