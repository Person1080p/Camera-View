using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CameraViewer.Api.Auth;
using CameraViewer.Api.Data;
using CameraViewer.Api.Endpoints;
using CameraViewer.Api.Models;
using CameraViewer.Api.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Machine-specific settings (seed devices with passwords, etc.) live in an untracked file.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

var paths = new AppPaths(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(paths);

// One server per data folder: a second copy would share the database and clean up the first one's live streams.
FileStream instanceLock;
try
{
    instanceLock = new FileStream(Path.Combine(paths.DataDir, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
catch (IOException)
{
    Console.Error.WriteLine($"Another Camera Viewer server is already running with data folder {paths.DataDir}. " +
        "Stop it first, or give this one a different Storage:DataPath.");
    return 1;
}

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={paths.DatabasePath}"));
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("CameraViewer")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDir));
// The keys guard the stored device passwords, so encrypt them at rest with the Windows user's DPAPI key.
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

builder.Services.AddSingleton<DeviceSecrets>();
builder.Services.AddSingleton<TailscaleService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TailscaleService>());
builder.Services.AddSingleton<IsapiClient>();
builder.Services.AddSingleton<NvrAdmin>();
builder.Services.AddSingleton<FfmpegLocator>();
builder.Services.AddSingleton<PlaybackSlots>();
builder.Services.AddScoped<DeviceAccess>();
builder.Services.AddSingleton<StreamManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamManager>());
builder.Services.AddSingleton<ExportService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ExportService>());

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "cameraviewer.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        // This is an API + SPA: answer with status codes instead of redirecting to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        o.Events.OnValidatePrincipal = AuthExtensions.ValidatePrincipalAsync;
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy("Admin", p => p.RequireRole(Roles.Admin));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, message) = ex switch
    {
        AppException a => (a.StatusCode, a.Message),
        BadHttpRequestException b => (b.StatusCode, "Invalid request"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected server error"),
    };
    ctx.Response.StatusCode = status;
    await ctx.Response.WriteAsJsonAsync(new { error = message });
}));

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();

// Until the initial/reset password is changed, only the auth endpoints are usable.
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.MustChangePassword() &&
        ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/auth"))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { error = "You must change your password first" });
        return;
    }
    await next();
});

app.UseAuthorization();

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapDeviceEndpoints();
app.MapStreamEndpoints();
app.MapExportEndpoints();
app.MapNvrAdminEndpoints();
app.MapTailscaleEndpoints();
app.Map("/api/{**rest}", () => Results.NotFound(new { error = "Not found" }));
app.MapFallbackToFile("index.html").AllowAnonymous();

await Seeder.SeedAsync(app.Services, app.Configuration, app.Logger);
FfmpegProcess.KillOrphans([paths.HlsDir, paths.ExportsDir], app.Logger);

var ffmpegPath = app.Services.GetRequiredService<FfmpegLocator>().Path;
if (ffmpegPath is null)
    app.Logger.LogWarning("ffmpeg not found: live view, playback and export will not work until it is installed");
else
    app.Logger.LogInformation("Using ffmpeg at {Path}", ffmpegPath);

app.Run();
GC.KeepAlive(instanceLock);
return 0;
