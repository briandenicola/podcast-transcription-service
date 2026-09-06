using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Components;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Endpoints;
using PodcastTranscription.Web.Services;
using PodcastTranscription.Web.Services.Ingest;
using PodcastTranscription.Web.Services.Maintenance;
using PodcastTranscription.Web.Services.Search;
using PodcastTranscription.Web.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(ResolveLogDirectory(context.Configuration, context.HostingEnvironment), "app-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        restrictedToMinimumLevel: LogEventLevel.Information));

builder.Services.Configure<WhisperOptions>(builder.Configuration.GetSection(WhisperOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<MediaToolOptions>(builder.Configuration.GetSection(MediaToolOptions.SectionName));
builder.Services.Configure<TranscriptionOptions>(builder.Configuration.GetSection(TranscriptionOptions.SectionName));
builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<MaintenanceOptions>(builder.Configuration.GetSection(MaintenanceOptions.SectionName));

var storage = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
var dataDirectory = Path.IsPathRooted(storage.DataPath)
    ? storage.DataPath
    : Path.Combine(builder.Environment.ContentRootPath, storage.DataPath);
Directory.CreateDirectory(dataDirectory);

// Components get their own short-lived context from the factory; scoped services keep the
// familiar injected AppDbContext. Blazor circuits outlive a request, so a single scoped
// context shared across a page's lifetime would be a concurrency bug waiting to happen.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataDirectory, "app.db")}"));
builder.Services.AddScoped<AppDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

var whisper = builder.Configuration.GetSection(WhisperOptions.SectionName).Get<WhisperOptions>() ?? new WhisperOptions();
builder.Services.AddHttpClient<WhisperClient>(client =>
{
    client.BaseAddress = new Uri(whisper.BaseUrl.TrimEnd('/') + "/");

    // The default 100 seconds will bite on the first long file.
    client.Timeout = TimeSpan.FromMinutes(whisper.RequestTimeoutMinutes);
});

builder.Services.AddSingleton<AdminAuthenticator>();

builder.Services.AddScoped<MediaStore>();
builder.Services.AddScoped<AudioProcessor>();
builder.Services.AddScoped<EpisodeImporter>();
builder.Services.AddScoped<TranscriptionPipeline>();
builder.Services.AddScoped<JobQueue>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<YtDlpClient>();
builder.Services.AddScoped<FeedService>();
builder.Services.AddScoped<MaintenanceService>();
builder.Services.AddHttpClient(nameof(FeedService));

// Shared across circuits and the worker, so both sides see the same running jobs and the same
// stream of progress events.
builder.Services.AddSingleton<RunningJobs>();
builder.Services.AddSingleton<JobNotifier>();

builder.Services.AddHostedService<TranscriptionWorker>();
builder.Services.AddHostedService<FeedPoller>();
builder.Services.AddHostedService<MaintenanceWorker>();

var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

builder.Services
    .AddAuthentication(AdminAuthenticator.CookieScheme)
    .AddCookie(AdminAuthenticator.CookieScheme, options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(Math.Clamp(authOptions.SessionDays, 1, 365));
        options.SlidingExpiration = true;
        options.Cookie.Name = "podcast-transcription.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;

        // Works over plain HTTP on a LAN, and upgrades itself the moment the app is behind TLS.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

var authorization = builder.Services.AddAuthorizationBuilder();

if (authOptions.Enabled)
{
    // A fallback policy protects every endpoint that does not opt out — pages, media streaming
    // and exports alike. Opting in page by page would eventually miss one.
    authorization.SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
}

builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        // Blazor streams uploads over the circuit; the 32 KB default is uncomfortably tight.
        options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
    });

var app = builder.Build();

// Fail closed. Starting with authentication on but no way to satisfy it would either lock the
// operator out or, worse, quietly leave the library open.
{
    var admin = app.Services.GetRequiredService<AdminAuthenticator>();

    if (admin.Enabled && !admin.HasPassword)
    {
        app.Logger.LogCritical(
            "Authentication is enabled but no password is configured. Set Auth__Password (or "
            + "Auth__PasswordHash, which is preferred) and restart, or set Auth__Enabled=false if "
            + "something in front of this app already authenticates.");

        throw new InvalidOperationException(
            "Auth is enabled but no password is configured. Set Auth__Password or Auth__PasswordHash.");
    }

    if (!admin.Enabled)
    {
        app.Logger.LogWarning(
            "Authentication is DISABLED. Anyone who can reach this app can read and delete the library.");
    }
}

await app.MigrateAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// CSS, JS and the favicon must load before anyone has signed in, or the login page arrives
// unstyled and without the Blazor script. The fallback policy would otherwise cover these too.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapMediaEndpoints();
app.MapExportEndpoints();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapEpisodeApiEndpoints();

app.Run();

static string ResolveLogDirectory(IConfiguration configuration, IHostEnvironment environment)
{
    var configured = configuration[$"{StorageOptions.SectionName}:DataPath"] ?? "var/data";
    var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    return logs;
}
