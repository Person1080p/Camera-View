namespace CameraViewer.Api.Services;

public enum SlotKind { Playback, Export }

public sealed record SlotHolder(SlotKind Kind, string Id, int UserId, string Username)
{
    public string Describe() => Kind == SlotKind.Playback ? $"{Username}'s playback" : $"an export by {Username}";
}

/// <summary>
/// Hikvision NVRs cap simultaneous remote recording streams (often at just one) and answer
/// "453 Not Enough Bandwidth" beyond that. Playback sessions and exports therefore claim a
/// per-device slot first, so users get a clear message instead of a cryptic RTSP failure.
/// </summary>
public class PlaybackSlots
{
    private readonly Dictionary<int, List<SlotHolder>> _held = [];

    public bool TryAcquire(int deviceId, int limit, SlotHolder holder, out SlotHolder? blocker)
    {
        lock (_held)
        {
            var list = _held.TryGetValue(deviceId, out var l) ? l : _held[deviceId] = [];
            if (list.Any(h => h.Id == holder.Id) || list.Count < Math.Max(1, limit))
            {
                if (list.All(h => h.Id != holder.Id)) list.Add(holder);
                blocker = null;
                return true;
            }
            blocker = list[0];
            return false;
        }
    }

    public void Release(int deviceId, string holderId)
    {
        lock (_held)
        {
            if (_held.TryGetValue(deviceId, out var list)) list.RemoveAll(h => h.Id == holderId);
        }
    }
}
