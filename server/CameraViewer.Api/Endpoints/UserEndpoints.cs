using CameraViewer.Api.Auth;
using CameraViewer.Api.Data;
using CameraViewer.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CameraViewer.Api.Endpoints;

public record CreateUserRequest(string Username, string Password, string Role, int[]? DeviceIds);

/// <summary>Password is optional; when set, the user must change it at next login.</summary>
public record UpdateUserRequest(string Role, int[]? DeviceIds, string? Password);

public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").RequireAuthorization("Admin");

        group.MapGet("/", async (AppDbContext db) =>
            (await db.Users.Include(u => u.DeviceAccess).OrderBy(u => u.Username).ToListAsync())
                .Select(u => u.ToDto()));

        group.MapPost("/", async (CreateUserRequest req, AppDbContext db, IPasswordHasher<User> hasher) =>
        {
            var username = (req.Username ?? "").Trim();
            if (username.Length is < 2 or > 64)
                return Results.BadRequest(new { error = "Username must be 2-64 characters" });
            if (!Roles.IsValid(req.Role))
                return Results.BadRequest(new { error = "Role must be Admin or Viewer" });
            if ((req.Password ?? "").Length < AuthEndpoints.MinPasswordLength)
                return Results.BadRequest(new { error = $"Password must be at least {AuthEndpoints.MinPasswordLength} characters" });
            if (await db.Users.AnyAsync(u => u.Username == username))
                return Results.Conflict(new { error = "That username is already taken" });

            var user = new User { Username = username, Role = req.Role, MustChangePassword = true };
            user.PasswordHash = hasher.HashPassword(user, req.Password!);
            user.DeviceAccess = await ValidDeviceIds(db, req.DeviceIds)
                .Select(id => new UserDevice { DeviceId = id }).ToListAsync();
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Ok(user.ToDto());
        });

        group.MapPut("/{id:int}", async (int id, UpdateUserRequest req, HttpContext http, AppDbContext db,
            IPasswordHasher<User> hasher, IMemoryCache cache) =>
        {
            var user = await db.Users.Include(u => u.DeviceAccess).FirstOrDefaultAsync(u => u.Id == id);
            if (user is null) return Results.NotFound(new { error = "User not found" });
            if (!Roles.IsValid(req.Role))
                return Results.BadRequest(new { error = "Role must be Admin or Viewer" });
            if (id == http.User.UserId() && req.Role != Roles.Admin)
                return Results.BadRequest(new { error = "You cannot remove your own admin role" });

            var credentialsChanged = user.Role != req.Role;
            user.Role = req.Role;

            if (!string.IsNullOrEmpty(req.Password))
            {
                if (req.Password.Length < AuthEndpoints.MinPasswordLength)
                    return Results.BadRequest(new { error = $"Password must be at least {AuthEndpoints.MinPasswordLength} characters" });
                user.PasswordHash = hasher.HashPassword(user, req.Password);
                user.MustChangePassword = true;
                credentialsChanged = true;
            }

            var deviceIds = await ValidDeviceIds(db, req.DeviceIds).ToListAsync();
            user.DeviceAccess.RemoveAll(ud => !deviceIds.Contains(ud.DeviceId));
            foreach (var deviceId in deviceIds.Where(d => user.DeviceAccess.All(ud => ud.DeviceId != d)))
                user.DeviceAccess.Add(new UserDevice { UserId = user.Id, DeviceId = deviceId });

            if (credentialsChanged) user.RotateSecurityStamp(cache);
            await db.SaveChangesAsync();
            return Results.Ok(user.ToDto());
        });

        group.MapDelete("/{id:int}", async (int id, HttpContext http, AppDbContext db, IMemoryCache cache) =>
        {
            if (id == http.User.UserId())
                return Results.BadRequest(new { error = "You cannot delete your own account" });
            var user = await db.Users.FindAsync(id);
            if (user is null) return Results.NotFound(new { error = "User not found" });

            user.RotateSecurityStamp(cache);
            db.Users.Remove(user);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static IQueryable<int> ValidDeviceIds(AppDbContext db, int[]? ids)
    {
        var wanted = ids ?? [];
        return db.Devices.Where(d => wanted.Contains(d.Id)).Select(d => d.Id);
    }
}
