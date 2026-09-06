using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services.Security;

namespace PodcastTranscription.Tests;

/// <summary>
/// Sign-in spans two sources: the account in configuration and the accounts in the database. The
/// configured one is the way back in after a restore, so nothing in the database may shadow it.
/// </summary>
public class SignInServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public SignInServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = new AppDbContext(_dbOptions);
        db.Database.Migrate();
    }

    public void Dispose() => _connection.Dispose();

    private SignInService Create(AppDbContext db, AuthOptions? auth = null)
    {
        auth ??= new AuthOptions { Enabled = true, Username = "admin", Password = "admin-password" };

        var admin = new AdminAuthenticator(Options.Create(auth), NullLogger<AdminAuthenticator>.Instance);
        var users = new UserStore(db, NullLogger<UserStore>.Instance);

        return new SignInService(admin, users, NullLogger<SignInService>.Instance);
    }

    [Fact]
    public async Task The_configured_admin_still_signs_in()
    {
        await using var db = new AppDbContext(_dbOptions);

        var outcome = await Create(db).AuthenticateAsync("admin", "admin-password");

        Assert.True(outcome.Succeeded);
        Assert.Equal(UserRole.Admin, outcome.Role);
        Assert.Null(outcome.UserId);
        Assert.True(outcome.Principal!.IsInRole(Roles.Admin));
    }

    [Fact]
    public async Task A_database_account_signs_in_with_its_own_role()
    {
        await using var db = new AppDbContext(_dbOptions);
        var store = new UserStore(db, NullLogger<UserStore>.Instance);
        await store.CreateAsync("brian", "Brian D", "password123", UserRole.Member);

        var outcome = await Create(db).AuthenticateAsync("brian", "password123");

        Assert.True(outcome.Succeeded);
        Assert.Equal(UserRole.Member, outcome.Role);
        Assert.NotNull(outcome.UserId);

        var principal = outcome.Principal!;
        Assert.True(principal.IsInRole(Roles.Member));
        Assert.False(principal.IsInRole(Roles.Admin));
        Assert.Equal("brian", principal.Identity!.Name);
        Assert.Equal("Brian D", principal.FindFirstValue(SignInService.DisplayNameClaim));
    }

    [Fact]
    public async Task A_viewer_gets_only_the_viewer_role()
    {
        await using var db = new AppDbContext(_dbOptions);
        await new UserStore(db, NullLogger<UserStore>.Instance)
            .CreateAsync("reader", null, "password123", UserRole.Viewer);

        var outcome = await Create(db).AuthenticateAsync("reader", "password123");

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.Principal!.IsInRole(Roles.Member));
        Assert.False(outcome.Principal.IsInRole(Roles.Admin));
        Assert.True(outcome.Principal.IsInRole(Roles.Viewer));
    }

    [Fact]
    public async Task The_username_is_matched_regardless_of_case()
    {
        await using var db = new AppDbContext(_dbOptions);
        await new UserStore(db, NullLogger<UserStore>.Instance)
            .CreateAsync("Brian", null, "password123", UserRole.Member);

        Assert.True((await Create(db).AuthenticateAsync("brian", "password123")).Succeeded);
        Assert.True((await Create(db).AuthenticateAsync("BRIAN", "password123")).Succeeded);
    }

    [Theory]
    [InlineData("brian", "wrong-password")]
    [InlineData("nobody", "password123")]
    [InlineData("", "password123")]
    [InlineData("brian", "")]
    public async Task Bad_credentials_are_refused_without_saying_which_half_was_wrong(
        string username, string password)
    {
        await using var db = new AppDbContext(_dbOptions);
        await new UserStore(db, NullLogger<UserStore>.Instance)
            .CreateAsync("brian", null, "password123", UserRole.Member);

        var outcome = await Create(db).AuthenticateAsync(username, password);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Reason);
        Assert.Null(outcome.Principal);
    }

    [Fact]
    public async Task A_deactivated_account_is_refused_and_told_why()
    {
        await using var db = new AppDbContext(_dbOptions);
        var store = new UserStore(db, NullLogger<UserStore>.Instance);
        var user = (await store.CreateAsync("brian", null, "password123", UserRole.Member)).User!;
        await store.UpdateAsync(user.Id, null, UserRole.Member, isActive: false);

        var outcome = await Create(db).AuthenticateAsync("brian", "password123");

        Assert.False(outcome.Succeeded);

        // Worth saying: trying another password will not help.
        Assert.Contains("deactivated", outcome.Reason);
    }

    [Fact]
    public async Task A_database_account_cannot_take_over_the_configured_admins_name()
    {
        await using var db = new AppDbContext(_dbOptions);

        // Nothing stops the row existing; what matters is which password wins the name.
        await new UserStore(db, NullLogger<UserStore>.Instance)
            .CreateAsync("admin", null, "impostor-password", UserRole.Viewer);

        var configured = await Create(db).AuthenticateAsync("admin", "admin-password");

        Assert.True(configured.Succeeded);
        Assert.Equal(UserRole.Admin, configured.Role);
        Assert.Null(configured.UserId);
    }
}
