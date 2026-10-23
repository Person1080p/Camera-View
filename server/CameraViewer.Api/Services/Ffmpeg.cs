using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CameraViewer.Api.Services;

/// <summary>
/// On Windows, ties ffmpeg children to this process with a kill-on-close job object. Without it a
/// crashed server would leave ffmpeg running, still holding the NVR's (often single) playback slot.
/// </summary>
internal static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<IntPtr> Job = new(Create);

    public static void Attach(Process process)
    {
        if (OperatingSystem.IsWindows() && Job.Value != IntPtr.Zero)
            AssignProcessToJobObject(Job.Value, process.Handle);
    }

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var info = new ExtendedLimitInformation { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            return SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size) ? job : IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}

/// <summary>Finds ffmpeg: explicit config, then PATH, then winget's per-user install location.</summary>
public class FfmpegLocator(IConfiguration config)
{
    private readonly Lazy<string?> _path = new(() => Resolve(config["Ffmpeg:Path"]));

    public string? Path => _path.Value;

    public string Require() => Path ?? throw new AppException(
        "ffmpeg was not found. Install it ('winget install Gyan.FFmpeg' on Windows, 'sudo apt install ffmpeg' on Debian/Ubuntu) or set Ffmpeg:Path in appsettings.json.", StatusCodes.Status503ServiceUnavailable);

    private static string? Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;

        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => System.IO.Path.Combine(dir.Trim('"'), exe));

        if (OperatingSystem.IsWindows())
        {
            var winget = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet");
            candidates = candidates.Append(System.IO.Path.Combine(winget, "Links", exe));
            var packages = System.IO.Path.Combine(winget, "Packages");
            if (Directory.Exists(packages))
                candidates = candidates.Concat(Directory.EnumerateDirectories(packages, "Gyan.FFmpeg*")
                    .SelectMany(d => Directory.EnumerateFiles(d, exe, SearchOption.AllDirectories)));
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>A running ffmpeg child process with its stderr tail captured for error reporting.</summary>
public sealed partial class FfmpegProcess : IDisposable
{
    private const int MaxErrorLines = 15;
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _errors = new();

    private FfmpegProcess(Process process) => _process = process;

    /// <summary>Raised for each stdout line (used with "-progress pipe:1").</summary>
    public event Action<string>? OutputLine;

    /// <summary>
    /// Linux has no equivalent of the Windows kill-on-close job, so after a crash ffmpeg children can outlive
    /// the server (unless systemd's cgroup cleanup got them). Kill any still writing into our folders at startup.
    /// </summary>
    public static void KillOrphans(IEnumerable<string> ownedDirectories, ILogger log)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc")) return;
        var markers = ownedDirectories.ToArray();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(System.IO.Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) continue;
            string[] argv;
            try { argv = File.ReadAllText(System.IO.Path.Combine(dir, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (argv.Length == 0 || System.IO.Path.GetFileName(argv[0]) != "ffmpeg") continue;
            if (!argv.Any(a => markers.Any(m => a.StartsWith(m, StringComparison.Ordinal)))) continue;
            // Leave ffmpeg alone if its parent is still a live server (e.g. one accidentally started twice).
            if (ParentIsCameraViewer(dir)) continue;
            try
            {
                using var orphan = Process.GetProcessById(pid);
                orphan.Kill();
                log.LogWarning("Killed leftover ffmpeg process {Pid} from a previous run", pid);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private static bool ParentIsCameraViewer(string procDir)
    {
        try
        {
            // /proc/<pid>/stat is "pid (comm) state ppid ..."; comm may contain spaces, so parse after the last ')'.
            var stat = File.ReadAllText(System.IO.Path.Combine(procDir, "stat"));
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            var ppid = fields[1];
            var parent = File.ReadAllText($"/proc/{ppid}/cmdline").Replace('\0', ' ');
            return parent.Contains("CameraViewer.Api", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public static FfmpegProcess Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var wrapper = new FfmpegProcess(process);
        process.ErrorDataReceived += (sender, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            wrapper._errors.Enqueue(e.Data);
            while (wrapper._errors.Count > MaxErrorLines) wrapper._errors.TryDequeue(out _);
        };
        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data is not null) wrapper.OutputLine?.Invoke(e.Data);
        };

        process.Start();
        ChildProcessJob.Attach(process);
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return wrapper;
    }

    public bool HasExited
    {
        get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } }
    }

    public int? ExitCode => HasExited ? _process.ExitCode : null;

    /// <summary>Last few lines of ffmpeg's stderr with any embedded credentials removed.</summary>
    public string ErrorTail => Redact(string.Join("\n", _errors));

    /// <summary>Waits up to <paramref name="timeout"/>; returns true if the process has exited.</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _process.WaitForExitAsync(ct).WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }

    public static string Redact(string text) => CredentialPattern().Replace(text, "$1://***@");

    [GeneratedRegex(@"(rtsp|https?)://[^/\s@]+@")]
    private static partial Regex CredentialPattern();
}

public static class RtspUrls
{
    public static string Live(DeviceConnection c, int channel, bool sub) =>
        $"{Root(c)}/Streaming/Channels/{IsapiClient.StreamId(channel, sub)}";

    /// <summary>Hikvision serves recordings over RTSP at real-time pace between the given device-local times.</summary>
    public static string Playback(DeviceConnection c, int channel, DateTime start, DateTime end) =>
        $"{Root(c)}/Streaming/tracks/{IsapiClient.StreamId(channel, false)}?starttime={Format(start)}&endtime={Format(end)}";

    private static string Root(DeviceConnection c) =>
        $"rtsp://{Uri.EscapeDataString(c.Username)}:{Uri.EscapeDataString(c.Password)}@{c.Host}:{c.RtspPort}";

    private static string Format(DateTime t) => t.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}
