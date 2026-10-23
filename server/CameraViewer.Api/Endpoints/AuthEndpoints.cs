using CameraViewer.Api.Auth;
using CameraViewer.Api.Data;
using CameraViewer.Api.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CameraViewer.Api.Endpoints;

public record LoginRequest(string Username, string Password, bool RememberMe);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record UserDto(int Id, string Username, string Role, bool MustChangePassword, int[] DeviceIds);

public static class AuthEndpoints
{
    public const int MinPasswordLength = 8;

    public static UserDto ToDto(this User u) =>
        new(u.Id, u.Username, u.Role, u.MustChangePassword, u.DeviceAccess.Select(d => d.DeviceId).ToArray());

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", async (LoginRequest req, AppDbContext db, IPasswordHasher<User> hasher, HttpContext http) =>
        {
            var user = await db.Users.Include(u => u.DeviceAccess)
                .FirstOrDefaultAsync(u => u.Username == (req.Username ?? "").Trim());
            if (user is null)
            {
                // Hash anyway so response timing doesn't reveal which usernames exist.
                hasher.HashPassword(new User(), req.Password ?? "");
                return Results.Json(new { error = "Invalid username or password" }, statusCode: 401);
            }

            var result = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password ?? "");
            if (result == PasswordVerificationResult.Failed)
                return Results.Json(new { error = "Invalid username or password" }, statusCode: 401);
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = hasher.HashPassword(user, req.Password!);
                await db.SaveChangesAsync();
            }

            await http.SignInUserAsync(user, req.RememberMe);
            return Results.Ok(user.ToDto());
        }).AllowAnonymous().RequireRateLimiting("login");

        group.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapGet("/me", async (HttpContext http, AppDbContext db) =>
        {
            var id = http.User.UserId();
            var user = await db.Users.Include(u => u.DeviceAccess).FirstOrDefaultAsync(u => u.Id == id);
            return user is null ? Results.Unauthorized() : Results.Ok(user.ToDto());
        });

        group.MapPost("/change-password", async (ChangePasswordRequest req, HttpContext http, AppDbContext db,
            IPasswordHasher<User> hasher, IMemoryCache cache) =>
        {
            var id = http.User.UserId();
            var user = await db.Users.Include(u => u.DeviceAccess).FirstAsync(u => u.Id == id);

            if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
                return Results.BadRequest(new { error = "Current password is incorrect" });
            if ((req.NewPassword ?? "").Length < MinPasswordLength)
                return Results.BadRequest(new { error = $"New password must be at least {MinPasswordLength} characters" });
            if (req.NewPassword == req.CurrentPassword)
                return Results.BadRequest(new { error = "New password must be different from the current one" });

            user.PasswordHash = hasher.HashPassword(user, req.NewPassword!);
            user.MustChangePassword = false;
            user.RotateSecurityStamp(cache);
            await db.SaveChangesAsync();

            // Re-issue the cookie so this session survives the stamp rotation; other sessions are signed out.
            await http.SignInUserAsync(user, persistent: true);
            return Results.Ok(user.ToDto());
        });
    }
}
