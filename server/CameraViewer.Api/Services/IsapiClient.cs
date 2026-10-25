using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace CameraViewer.Api.Services;

public sealed record DeviceInfo(string Name, string Model, string SerialNumber, string Firmware, string DeviceType);

public sealed record ChannelInfo(int Id, string Name, bool Online);

/// <summary>Start/end are device-local wall-clock times.</summary>
public sealed record RecordingSegment(DateTime Start, DateTime End);

public class IsapiException(string message) : AppException(message, StatusCodes.Status502BadGateway);

/// <summary>
/// Minimal Hikvision ISAPI client (also works with the many OEM rebrands of Hikvision NVRs).
/// Authentication is HTTP digest, which SocketsHttpHandler negotiates automatically.
/// </summary>
public class IsapiClient(TailscaleService tailscale)
{
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    // Channel N's main stream is N01 and its sub stream N02; recorded tracks use the same numbering.
    public static int StreamId(int channel, bool sub) => channel * 100 + (sub ? 2 : 1);

    public async Task<DeviceInfo> GetDeviceInfoAsync(DeviceConnection c, CancellationToken ct)
    {
        var xml = await GetXmlAsync(c, "/ISAPI/System/deviceInfo", ct);
        var root = xml.Root!;
        return new DeviceInfo(
            root.Val("deviceName") ?? "",
            root.Val("model") ?? "",
            root.Val("serialNumber") ?? "",
            $"{root.Val("firmwareVersion")} {root.Val("firmwareReleasedDate")}".Trim(),
            root.Val("deviceType") ?? "");
    }

    /// <summary>
    /// NVRs list their cameras as "input proxy" channels; standalone cameras (and analog DVR inputs)
    /// appear under video inputs instead. Falls back to a single channel if neither is supported.
    /// </summary>
    public async Task<List<ChannelInfo>> GetChannelsAsync(DeviceConnection c, CancellationToken ct)
    {
        var proxy = await TryGetXmlAsync(c, "/ISAPI/ContentMgmt/InputProxy/channels", ct);
        var proxyChannels = proxy?.Els("InputProxyChannel").ToList() ?? [];
        if (proxyChannels.Count > 0)
        {
            var status = await TryGetXmlAsync(c, "/ISAPI/ContentMgmt/InputProxy/channels/status", ct);
            var online = status?.Els("InputProxyChannelStatus")
                .ToDictionary(e => e.Val("id") ?? "", e => e.Val("online") == "true") ?? [];

            return proxyChannels
                .Select(e => (Id: ParseInt(e.Val("id")), Name: e.Val("name")))
                .Where(x => x.Id > 0)
                .Select(x => new ChannelInfo(x.Id, NameOrDefault(x.Name, x.Id),
                    status is null || online.GetValueOrDefault(x.Id.ToString())))
                .OrderBy(x => x.Id)
                .ToList();
        }

        var inputs = await TryGetXmlAsync(c, "/ISAPI/System/Video/inputs/channels", ct);
        var inputChannels = inputs?.Els("VideoInputChannel")
            .Select(e => (Id: ParseInt(e.Val("id")), Name: e.Val("name"), Enabled: e.Val("videoInputEnabled")))
            .Where(x => x.Id > 0)
            .Select(x => new ChannelInfo(x.Id, NameOrDefault(x.Name, x.Id), x.Enabled != "false"))
            .ToList() ?? [];

        return inputChannels.Count > 0 ? inputChannels : [new ChannelInfo(1, "Channel 1", true)];
    }

    public async Task<(byte[] Bytes, string ContentType)> GetSnapshotAsync(DeviceConnection c, int channel, bool sub, CancellationToken ct)
    {
        var res = await SendAsync(c, HttpMethod.Get, $"/ISAPI/Streaming/channels/{StreamId(channel, sub)}/picture", null, ct);
        // Some DVRs only take pictures from the main stream.
        if (sub && res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            res.Dispose();
            res = await SendAsync(c, HttpMethod.Get, $"/ISAPI/Streaming/channels/{StreamId(channel, false)}/picture", null, ct);
        }
        using var _ = res;
        if (!res.IsSuccessStatusCode)
            throw new IsapiException($"Snapshot failed: HTTP {(int)res.StatusCode}");
        return (await res.Content.ReadAsByteArrayAsync(ct), res.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    /// <summary>Lists recorded spans on the channel's main track, merging back-to-back files.</summary>
    public async Task<List<RecordingSegment>> SearchRecordingsAsync(DeviceConnection c, int channel, DateTime from, DateTime to, CancellationToken ct)
    {
        const int pageSize = 50;
        var searchId = Guid.NewGuid().ToString().ToUpperInvariant();
        var found = new List<RecordingSegment>();

        for (var position = 0; position < pageSize * 40; position += pageSize)
        {
            var body = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <CMSearchDescription>
                  <searchID>{searchId}</searchID>
                  <trackList><trackID>{StreamId(channel, false)}</trackID></trackList>
                  <timeSpanList><timeSpan><startTime>{IsapiTime(from)}</startTime><endTime>{IsapiTime(to)}</endTime></timeSpan></timeSpanList>
                  <maxResults>{pageSize}</maxResults>
                  <searchResultPostion>{position}</searchResultPostion>
                  <metadataList><metadataDescriptor>//recordType.meta.std-cgi.com</metadataDescriptor></metadataList>
                </CMSearchDescription>
                """;
            using var res = await SendAsync(c, HttpMethod.Post, "/ISAPI/ContentMgmt/search",
                new StringContent(body, Encoding.UTF8, "application/xml"), ct);
            var xml = await ReadXmlAsync(res, ct);

            var items = xml.Els("searchMatchItem").ToList();
            foreach (var item in items)
            {
                var span = item.Els("timeSpan").FirstOrDefault();
                if (span is null) continue;
                if (TryParseDeviceTime(span.Val("startTime"), out var s) && TryParseDeviceTime(span.Val("endTime"), out var e) && e > s)
                    found.Add(new RecordingSegment(s, e));
            }

            var status = xml.Root?.Val("responseStatusStrg");
            if (items.Count == 0 || !string.Equals(status, "MORE", StringComparison.OrdinalIgnoreCase))
                break;
        }

        var merged = new List<RecordingSegment>();
        foreach (var seg in found.OrderBy(x => x.Start))
        {
            if (merged.Count > 0 && seg.Start <= merged[^1].End.AddSeconds(2))
            {
                if (seg.End > merged[^1].End) merged[^1] = merged[^1] with { End = seg.End };
            }
            else
            {
                merged.Add(seg);
            }
        }
        return merged;
    }

    // The recording index is in device-local time even though Hikvision suffixes it with "Z".
    private static string IsapiTime(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static bool TryParseDeviceTime(string? value, out DateTime time)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            time = DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified);
            return true;
        }
        time = default;
        return false;
    }

    private HttpClient Client(DeviceConnection c)
    {
        // Keyed on the connection details so that editing a device transparently yields a fresh client.
        var key = $"{c.Host}:{c.HttpPort}\n{c.Username}\n{c.Password}\n{c.ViaTailscale}";
        return _clients.GetOrAdd(key, _ =>
        {
            var handler = new SocketsHttpHandler
            {
                Credentials = new NetworkCredential(c.Username, c.Password),
                PreAuthenticate = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                // Remote sites are often on LTE; the first connection through the tailnet can take a while.
                ConnectTimeout = TimeSpan.FromSeconds(c.ViaTailscale ? 20 : 5),
            };
            if (c.ViaTailscale)
            {
                handler.UseProxy = false;
                handler.ConnectCallback = (ctx, ct) => new ValueTask<Stream>(tailscale.ConnectAsync(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, ct));
            }
            return new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://{c.Host}:{c.HttpPort}"),
                Timeout = TimeSpan.FromSeconds(c.ViaTailscale ? 40 : 20),
            };
        });
    }

    private async Task<HttpResponseMessage> SendAsync(DeviceConnection c, HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var via = c.ViaTailscale ? " through Tailscale" : "";
        try
        {
            return await Client(c).SendAsync(new HttpRequestMessage(method, path) { Content = content }, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AppException inner)
        {
            throw inner; // e.g. "Tailscale is turned off"
        }
        catch (HttpRequestException ex)
        {
            throw new IsapiException($"Cannot reach {c.Host}:{c.HttpPort}{via} ({ex.InnerException?.Message ?? ex.Message})");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IsapiException($"Timed out contacting {c.Host}:{c.HttpPort}{via}");
        }
    }

    public async Task<XDocument> GetXmlAsync(DeviceConnection c, string path, CancellationToken ct)
    {
        using var res = await SendAsync(c, HttpMethod.Get, path, null, ct);
        return await ReadXmlAsync(res, ct);
    }

    /// <summary>
    /// Sends a configuration change (PUT/POST/DELETE) and checks the device's ResponseStatus.
    /// Returns the ResponseStatus document (e.g. it carries the new id after creating a user).
    /// </summary>
    public async Task<XDocument> SendXmlAsync(DeviceConnection c, HttpMethod method, string path, XDocument? body, CancellationToken ct)
    {
        HttpContent? content = body is null
            ? null
            : new StringContent($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>{body.Root!.ToString(SaveOptions.DisableFormatting)}", Encoding.UTF8, "application/xml");
        using var res = await SendAsync(c, method, path, content, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new IsapiException("Device rejected the username or password");

        var text = await res.Content.ReadAsStringAsync(ct);
        var xml = TryParse(text);
        var status = xml?.Root is { } root && root.Name.LocalName == "ResponseStatus" ? root : null;
        var code = status?.Val("statusCode");
        // 1 = OK, 7 = OK but the device must reboot for the change to apply.
        if (res.IsSuccessStatusCode && (code is null or "1" or "7"))
            return xml ?? new XDocument();

        var reason = status is null
            ? $"HTTP {(int)res.StatusCode}"
            : $"{status.Val("statusString")} ({status.Val("subStatusCode")})";
        throw new IsapiException($"The device refused the change: {reason}");
    }

    private async Task<XDocument?> TryGetXmlAsync(DeviceConnection c, string path, CancellationToken ct)
    {
        using var res = await SendAsync(c, HttpMethod.Get, path, null, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new IsapiException("Device rejected the username or password");
        if (!res.IsSuccessStatusCode) return null;
        var xml = XDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return xml.Root?.Name.LocalName == "ResponseStatus" ? null : xml;
    }

    private static async Task<XDocument> ReadXmlAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new IsapiException("Device rejected the username or password");
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var reason = TryParse(text)?.Root?.Val("statusString");
            throw new IsapiException($"Device returned HTTP {(int)res.StatusCode}{(reason is null ? "" : $" ({reason})")}");
        }
        return TryParse(text) ?? throw new IsapiException("Device returned an unreadable response");
    }

    private static XDocument? TryParse(string text)
    {
        try { return XDocument.Parse(text); }
        catch (System.Xml.XmlException) { return null; }
    }

    private static int ParseInt(string? s) => int.TryParse(s, out var v) ? v : 0;

    private static string NameOrDefault(string? name, int id) => string.IsNullOrWhiteSpace(name) ? $"Channel {id}" : name.Trim();
}

internal static class XmlExtensions
{
    // ISAPI responses use several different namespaces across firmware versions, so match on local names.
    public static IEnumerable<XElement> Els(this XContainer x, string name) =>
        x.Descendants().Where(e => e.Name.LocalName == name);

    public static string? Val(this XElement e, string name) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value;

    public static XElement? El(this XElement e, string name) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName == name);

    /// <summary>Sets (or adds, in the parent's namespace) a child element's value; XElement escapes it.</summary>
    public static void SetVal(this XElement e, string name, string value)
    {
        var child = e.El(name);
        if (child is null) e.Add(new XElement(e.Name.Namespace + name, value));
        else child.Value = value;
    }

    /// <summary>The "opt" list on a capabilities element, e.g. opt="H.264,H.265".</summary>
    public static string[] Opts(this XElement? e) =>
        e?.Attribute("opt")?.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}
