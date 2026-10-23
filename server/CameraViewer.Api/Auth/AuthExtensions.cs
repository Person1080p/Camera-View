using System.Security.Claims;
using CameraViewer.Api.Data;
using CameraViewer.Api.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CameraViewer.Api.Auth;

public static class AuthExtensions
{
    private const string StampClaim = "stamp";
    private const string MustChangeClaim = "must_change_password";

    public static int UserId(this ClaimsPrincipal p) => int.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static bool IsAdmin(this ClaimsPrincipal p) => p.IsInRole(Roles.Admin);

    public static bool MustChangePassword(this ClaimsPrincipal p) => p.HasClaim(MustChangeClaim, "true");

    public static Task SignInUserAsync(this HttpContext http, User user, bool persistent) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim(StampClaim, user.SecurityStamp),
                new Claim(MustChangeClaim, user.MustChangePassword ? "true" : "false"),
            ], CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = persistent });

    /// <summary>Rejects cookies of deleted users or users whose password/role changed since sign-in.</summary>
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        if (ctx.Principal is null || !int.TryParse(ctx.Principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        {
            ctx.RejectPrincipal();
            return;
        }

        var cache = ctx.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();
        var stamp = await cache.GetOrCreateAsync(StampCacheKey(id), async entry =>
        {
            // Video players fetch a segment every second or two, so avoid a database hit per request.
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            return await db.Users.Where(u => u.Id == id).Select(u => u.SecurityStamp).FirstOrDefaultAsync();
        });

        if (stamp is null || stamp != ctx.Principal.FindFirstValue(StampClaim))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    /// <summary>Call after changing a user's password, role or deleting them.</summary>
    public static void RotateSecurityStamp(this User user, IMemoryCache cache)
    {
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        cache.Remove(StampCacheKey(user.Id));
    }

    private static string StampCacheKey(int userId) => $"user-stamp:{userId}";
}
