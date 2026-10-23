using CameraViewer.Api.Models;
using CameraViewer.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CameraViewer.Api.Data;

public static class Seeder
{
    private sealed class SeedDevice
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int HttpPort { get; set; } = 80;
        public int RtspPort { get; set; } = 554;
        public string Username { get; set; } = "admin";
        public string Password { get; set; } = "";
        public bool UseTailscale { get; set; }
    }

    /// <summary>
    /// EnsureCreated only builds a fresh database, so columns added in later versions are added here to
    /// existing ones. Each entry is idempotent.
    /// </summary>
    private static readonly (string Table, string Column, string Definition)[] AddedColumns =
    [
        ("Devices", "UseTailscale", "INTEGER NOT NULL DEFAULT 0"),
    ];

    private static async Task UpgradeSchemaAsync(AppDbContext db, ILogger log)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        foreach (var (table, column, definition) in AddedColumns)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            if (Convert.ToInt64(await check.ExecuteScalarAsync()) > 0) continue;

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
            await alter.ExecuteNonQueryAsync();
            log.LogInformation("Upgraded database: added {Table}.{Column}", table, column);
        }
    }

    /// <summary>
    /// Creates the database, an initial admin (who must change the password on first login), and any
    /// devices listed under Seed:Devices. Devices are only seeded into an empty device table.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services, IConfiguration config, ILogger log)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        await UpgradeSchemaAsync(db, log);

        if (!await db.Users.AnyAsync())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
            var admin = new User
            {
                Username = config["Seed:AdminUsername"] ?? "admin",
                Role = Roles.Admin,
                MustChangePassword = true,
            };
            admin.PasswordHash = hasher.HashPassword(admin, config["Seed:AdminPassword"] ?? "admin");
            db.Users.Add(admin);
            log.LogWarning("Created initial admin account '{User}'; its password must be changed at first login", admin.Username);
        }

        if (!await db.Devices.AnyAsync())
        {
            var secrets = scope.ServiceProvider.GetRequiredService<DeviceSecrets>();
            foreach (var d in config.GetSection("Seed:Devices").Get<List<SeedDevice>>() ?? [])
            {
                db.Devices.Add(new Device
                {
                    Name = d.Name,
                    Host = d.Host,
                    HttpPort = d.HttpPort,
                    RtspPort = d.RtspPort,
                    Username = d.Username,
                    UseTailscale = d.UseTailscale,
                    ProtectedPassword = secrets.Protect(d.Password),
                });
                log.LogInformation("Added device '{Name}' ({Host}) from configuration", d.Name, d.Host);
            }
        }

        await db.SaveChangesAsync();
    }
}
