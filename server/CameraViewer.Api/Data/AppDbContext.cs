using CameraViewer.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CameraViewer.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<UserDevice> UserDevices => Set<UserDevice>();
    public DbSet<ExportJob> ExportJobs => Set<ExportJob>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().Property(u => u.Username).UseCollation("NOCASE");
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();

        b.Entity<UserDevice>().HasKey(x => new { x.UserId, x.DeviceId });
        b.Entity<UserDevice>()
            .HasOne(x => x.User).WithMany(u => u.DeviceAccess)
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserDevice>()
            .HasOne(x => x.Device).WithMany()
            .HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<ExportJob>().Property(x => x.Status).HasConversion<string>();
        // SQLite drops DateTimeKind; mark these as UTC so they serialize with a "Z".
        b.Entity<ExportJob>().Property(x => x.CreatedAt)
            .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        b.Entity<ExportJob>().Property(x => x.CompletedAt)
            .HasConversion(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : null);
        b.Entity<ExportJob>().HasIndex(x => x.UserId);
    }
}
