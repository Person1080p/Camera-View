using System.Text.RegularExpressions;
using CameraViewer.Api.Services;

namespace CameraViewer.Api.Endpoints;

public record TailscaleSettingsRequest(bool Enabled, string Hostname);
public record TailscaleLoginRequest(string? AuthKey);
public record TailscaleRoutesRequest(bool AcceptRoutes);

/// <summary>Controls the app's embedded Tailscale node. Admin-only.</summary>
public static partial class TailscaleEndpoints
{
    public static void MapTailscaleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tailscale").RequireAuthorization("Admin");

        group.MapGet("/", (TailscaleService ts, CancellationToken ct) => ts.GetStatusAsync(ct));

        group.MapPut("/", async (TailscaleSettingsRequest req, TailscaleService ts, HttpContext http, ILoggerFactory logs, CancellationToken ct) =>
        {
            var hostname = (req.Hostname ?? "").Trim().ToLowerInvariant();
            if (!MachineName().IsMatch(hostname))
                throw new AppException("The machine name must be 1-63 letters, digits or hyphens, and can't start or end with a hyphen");
            await ts.UpdateSettingsAsync(req.Enabled, hostname, ct);
            Audit(logs, http, $"turned Tailscale {(req.Enabled ? "on" : "off")} as '{hostname}'");
            return await ts.GetStatusAsync(ct);
        });

        group.MapPost("/login", async (TailscaleLoginRequest req, TailscaleService ts, HttpContext http, ILoggerFactory logs, CancellationToken ct) =>
        {
            var key = req.AuthKey?.Trim();
            if (!string.IsNullOrEmpty(key) && !key.StartsWith("tskey-", StringComparison.Ordinal))
                throw new AppException("Tailscale auth keys start with \"tskey-\"");
            await ts.LoginAsync(key, ct);
            Audit(logs, http, string.IsNullOrEmpty(key) ? "started Tailscale sign-in" : "signed Tailscale in with an auth key");
            return Results.NoContent();
        });

        group.MapPost("/logout", async (TailscaleService ts, HttpContext http, ILoggerFactory logs, CancellationToken ct) =>
        {
            await ts.LogoutAsync(ct);
            Audit(logs, http, "signed Tailscale out");
            return Results.NoContent();
        });

        group.MapPut("/routes", async (TailscaleRoutesRequest req, TailscaleService ts, HttpContext http, ILoggerFactory logs, CancellationToken ct) =>
        {
            await ts.SetAcceptRoutesAsync(req.AcceptRoutes, ct);
            Audit(logs, http, $"{(req.AcceptRoutes ? "enabled" : "disabled")} Tailscale subnet routes");
            return Results.NoContent();
        });
    }

    private static void Audit(ILoggerFactory logs, HttpContext http, string action) =>
        logs.CreateLogger("CameraViewer.Audit").LogWarning("{User} {Action}", http.User.Identity?.Name, action);

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex MachineName();
}
