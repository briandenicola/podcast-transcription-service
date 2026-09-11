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
using PodcastTranscription.Web.Services.Summarization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, config) =>
{
    config
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console();

    // Null when the directory could not be created — an unwritable data volume, most likely.
    // The console sink carries on alone rather than the app dying before it can report why.
    if (ResolveLogDirectory(context.Configuration, context.HostingEnvironment) is { } logDirectory)
    {
        config.WriteTo.File(
            Path.Combine(logDirectory, "app-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            restrictedToMinimumLevel: LogEventLevel.Information);
    }
});

builder.Services.Configure<WhisperOptions>(builder.Configuration.GetSection(WhisperOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<MediaToolOptions>(builder.Configuration.GetSection(MediaToolOptions.SectionName));
builder.Services.Configure<TranscriptionOptions>(builder.Configuration.GetSection(TranscriptionOptions.SectionName));
builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<MaintenanceOptions>(builder.Configuration.GetSection(MaintenanceOptions.SectionName));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));

var storage = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
var dataDirectory = Path.IsPathRooted(storage.DataPath)
    ? storage.DataPath
    : Path.Combine(builder.Environment.ContentRootPath, storage.DataPath);
Directory.CreateDirectory(dataDirectory);

// Components get their own short-lived context from the factory; scoped services keep the
// familiar injected AppDbContext. Blazor circuits outlive a request, so a single scoped
// context shared across a page's lifetime would be a concurrency bug waiting to happen.
// A podcast episode is far larger than the 30 MB Kestrel and the 128 MB form parser allow by
// default, so both ceilings follow the configured limit.
var maxUploadBytes = (long)storage.MaxUploadMb * 1024 * 1024;

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxUploadBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
    options.ValueLengthLimit = int.MaxValue;
});

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

var ollama = builder.Configuration.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>() ?? new OllamaOptions();
builder.Services.AddHttpClient<OllamaClient>(client =>
{
    client.BaseAddress = new Uri(ollama.BaseUrl.TrimEnd('/') + "/");

    // A long episode is several generate calls in a row, and a model running on CPU takes
    // minutes over each one. The 100 second default abandons all of them.
    client.Timeout = TimeSpan.FromMinutes(ollama.RequestTimeoutMinutes);
});

builder.Services.AddSingleton<AdminAuthenticator>();

builder.Services.AddScoped<MediaStore>();
builder.Services.AddScoped<AudioProcessor>();
builder.Services.AddScoped<EpisodeImporter>();
builder.Services.AddScoped<TranscriptionPipeline>();
builder.Services.AddScoped<JobQueue>();
builder.Services.AddScoped<DeletionService>();
builder.Services.AddScoped<EpisodeMetadataService>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<YtDlpClient>();
builder.Services.AddScoped<FeedService>();
builder.Services.AddScoped<PodcastUrlResolver>();
builder.Services.AddScoped<MaintenanceService>();
builder.Services.AddScoped<UserStore>();
builder.Services.AddScoped<SignInService>();
builder.Services.AddScoped<TranscriptSummarizer>();
builder.Services.AddScoped<SummaryService>();
builder.Services.AddScoped<PushoverNotifier>();
builder.Services.AddHttpClient(nameof(FeedService));
builder.Services.AddHttpClient(nameof(PushoverNotifier));

// Shared across circuits and the worker, so both sides see the same running jobs and the same
// stream of progress events.
builder.Services.AddSingleton<RunningJobs>();
builder.Services.AddSingleton<JobNotifier>();

// Summarising outlives the request that starts it, so the state has to be a singleton too.
builder.Services.AddSingleton<SummaryRunner>();

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

// Two policies above the fallback. Viewers satisfy only the fallback: they read the library and
// change nothing. Members add the work — queuing, correcting, summarising, feeds — and admins add
// everything that destroys or reconfigures, including other people's accounts.
authorization.AddPolicy(Roles.AdminPolicy, policy => policy.RequireRole(Roles.Admin));
authorization.AddPolicy(Roles.MemberPolicy, policy => policy.RequireRole(Roles.MemberOrAbove));

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
        // Names the compose variable as well as the raw setting: with Docker, ADMIN_PASSWORD in
        // .env is what an operator actually edits, and Auth__Password is only how it arrives.
        app.Logger.LogCritical(
            "Authentication is enabled but no password is configured.\n"
            + "  Running with Docker: copy .env.example to .env and set ADMIN_PASSWORD, then "
            + "`docker compose up -d`.\n"
            + "  Running directly:    set Auth__Password, or Auth__PasswordHash which is preferred.\n"
            + "  Already behind a proxy that authenticates? Set Auth__Enabled=false (AUTH_ENABLED=false "
            + "in .env).");

        throw new InvalidOperationException(
            "Auth is enabled but no password is configured. Set ADMIN_PASSWORD in .env "
            + "(or Auth__Password / Auth__PasswordHash directly).");
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

// Re-executing to a friendly page is right for someone browsing, and actively misleading for
// everything else: it turned an unauthenticated /_blazor/negotiate into a 404, which reads like
// a routing bug rather than an expired session.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/_blazor")
            && !context.Request.Path.StartsWithSegments("/_framework")
            && !context.Request.Path.StartsWithSegments("/api")
            && !context.Request.Path.StartsWithSegments("/healthz"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// CSS, JS and the favicon must load before anyone has signed in, or the login page arrives
// unstyled and without the Blazor script. The fallback policy would otherwise cover these too.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapUploadEndpoints();
app.MapDeletionEndpoints();
app.MapActionEndpoints();
app.MapUserEndpoints();
app.MapMediaEndpoints();
app.MapExportEndpoints();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapEpisodeApiEndpoints();

app.Run();

// Returns null when the log directory cannot be created, in which case file logging is skipped
// and the console sink carries on alone. A permissions problem on the data volume is a real
// failure, but it should surface as a clear database error rather than as a logging stack trace
// thrown before the logger even exists.
static string? ResolveLogDirectory(IConfiguration configuration, IHostEnvironment environment)
{
    var configured = configuration[$"{StorageOptions.SectionName}:DataPath"] ?? "var/data";
    var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
    var logs = Path.Combine(root, "logs");

    try
    {
        Directory.CreateDirectory(logs);
        return logs;
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
        Console.Error.WriteLine($"Could not create the log directory '{logs}': {ex.Message}. Logging to console only.");
        return null;
    }
}
