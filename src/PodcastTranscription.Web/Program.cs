using Microsoft.EntityFrameworkCore;
using PodcastTranscription.Web.Components;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Endpoints;
using PodcastTranscription.Web.Services;
using PodcastTranscription.Web.Services.Search;
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

builder.Services.AddScoped<MediaStore>();
builder.Services.AddScoped<AudioProcessor>();
builder.Services.AddScoped<EpisodeImporter>();
builder.Services.AddScoped<TranscriptionPipeline>();
builder.Services.AddScoped<JobQueue>();
builder.Services.AddScoped<SearchService>();

// Shared across circuits and the worker, so both sides see the same running jobs and the same
// stream of progress events.
builder.Services.AddSingleton<RunningJobs>();
builder.Services.AddSingleton<JobNotifier>();

builder.Services.AddHostedService<TranscriptionWorker>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        // Blazor streams uploads over the circuit; the 32 KB default is uncomfortably tight.
        options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
    });

var app = builder.Build();

await app.MigrateAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseSerilogRequestLogging();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapMediaEndpoints();
app.MapExportEndpoints();

app.MapGet("/healthz", async (WhisperClient client) =>
{
    var reachable = await client.IsReachableAsync();
    return Results.Json(
        new { status = reachable ? "healthy" : "degraded", whisper = new { endpoint = client.Endpoint, reachable } },
        statusCode: reachable ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.Run();

static string ResolveLogDirectory(IConfiguration configuration, IHostEnvironment environment)
{
    var configured = configuration[$"{StorageOptions.SectionName}:DataPath"] ?? "var/data";
    var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    return logs;
}
