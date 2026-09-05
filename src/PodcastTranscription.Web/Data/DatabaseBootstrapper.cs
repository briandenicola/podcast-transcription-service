using Microsoft.EntityFrameworkCore;

namespace PodcastTranscription.Web.Data;

public static class DatabaseBootstrapper
{
    /// <summary>
    /// Applies migrations on startup and puts SQLite into WAL mode, which lets the UI read
    /// while the worker writes. Both are idempotent.
    /// </summary>
    public static async Task MigrateAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Database");

        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");

        // SQLite has a single writer; wait rather than throwing when the worker holds it.
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=10000;");

        log.LogInformation("Database ready at {DataSource}", db.Database.GetDbConnection().DataSource);
    }
}
