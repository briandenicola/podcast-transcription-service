using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Services.Security;

namespace PodcastTranscription.Tests;

public class UserStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public UserStoreTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = new AppDbContext(_dbOptions);
        db.Database.Migrate();
    }

    public void Dispose() => _connection.Dispose();

    private UserStore Create(AppDbContext db) => new(db, NullLogger<UserStore>.Instance);

    [Fact]
    public async Task A_created_account_stores_a_hash_and_never_the_password()
    {
        await using var db = new AppDbContext(_dbOptions);

        var result = await Create(db).CreateAsync("brian", "Brian", "correct-horse", UserRole.Member);

        Assert.True(result.Success);

        var stored = await db.Users.SingleAsync();
        Assert.Equal("brian", stored.Username);
        Assert.Equal(UserRole.Member, stored.Role);
        Assert.True(stored.IsActive);

        Assert.DoesNotContain("correct-horse", stored.PasswordHash);
        Assert.True(PasswordHasher.Verify("correct-horse", stored.PasswordHash));
    }

    [Fact]
    public async Task Usernames_collide_regardless_of_case()
    {
        await using var db = new AppDbContext(_dbOptions);
        var store = Create(db);

        await store.CreateAsync("brian", null, "password123", UserRole.Viewer);
        var second = await store.CreateAsync("BRIAN", null, "password123", UserRole.Admin);

        Assert.False(second.Success);
        Assert.Contains("already an account", second.Message);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task An_account_is_found_regardless_of_the_case_typed_at_sign_in()
    {
        await using var db = new AppDbContext(_dbOptions);
        var store = Create(db);

        await store.CreateAsync("Brian", null, "password123", UserRole.Viewer);

        Assert.NotNull(await store.FindByNameAsync("brian"));
        Assert.NotNull(await store.FindByNameAsync("BRIAN"));
        Assert.NotNull(await store.FindByNameAsync("  Brian  "));
        Assert.Null(await store.FindByNameAsync("someone-else"));
    }

    [Theory]
    [InlineData("", "password123", "username is required")]
    [InlineData("has space", "password123", "letters, digits")]
    [InlineData("semi;colon", "password123", "letters, digits")]
    [InlineData("brian", "short", "at least 8")]
    [InlineData("brian", "", "at least 8")]
    public async Task Bad_input_is_refused_with_a_reason(string username, string password, string expected)
    {
        await using var db = new AppDbContext(_dbOptions);

        var result = await Create(db).CreateAsync(username, null, password, UserRole.Viewer);

        Assert.False(result.Success);
        Assert.Contains(expected, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.Users);
    }

    [Theory]
    [InlineData("dots.and-dashes_ok")]
    [InlineData("Brian2")]
    public async Task Reasonable_usernames_are_accepted(string username)
    {
        await using var db = new AppDbContext(_dbOptions);

        Assert.True((await Create(db).CreateAsync(username, null, "password123", UserRole.Viewer)).Success);
    }

    [Fact]
    public async Task Updating_changes_role_and_state_but_leaves_the_password_alone()
    {
        await using var db = new AppDbContext(_dbOptions);
        var store = Create(db);

        var created = (await store.CreateAsync("brian", null, "password123", UserRole.Viewer)).User!;
        var hash = created.PasswordHash;

        await store.UpdateAsync(created.Id, "Brian D", UserRole.Admin, isActive: false);

        var stored = await db.Users.SingleAsync();
        Assert.Equal(UserRole.Admin, stored.Role);
        Assert.False(stored.IsActive);
        Assert.Equal("Brian D", stored.DisplayName);
        Assert.Equal(hash, stored.PasswordHash);
    }

    [Fact]
    public async Task Setting_a_password_replaces_the_hash_and_still_enforces_a_length()
    {
        await using var db = new AppDbContext(_dbOptions);
        var store = Create(db);

        var created = (await store.CreateAsync("brian", null, "password123", UserRole.Viewer)).User!;

        Assert.False((await store.SetPasswordAsync(created.Id, "tiny")).Success);
        Assert.True(PasswordHasher.Verify("password123", (await db.Users.SingleAsync()).PasswordHash));

        Assert.True((await store.SetPasswordAsync(created.Id, "a-longer-one")).Success);

        var stored = await db.Users.SingleAsync();
        Assert.True(PasswordHasher.Verify("a-longer-one", stored.PasswordHash));
        Assert.False(PasswordHasher.Verify("password123", stored.PasswordHash));
    }

    [Fact]
    public async Task Deleting_an_account_leaves_what_it_did_behind()
    {
        await using var db = new AppDbContext(_dbOptions);
        var store = Create(db);

        var created = (await store.CreateAsync("brian", null, "password123", UserRole.Member)).User!;

        var episode = new Episode { Title = "E", AudioPath = "a.mp3", AudioSha256 = new string('a', 64) };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        db.Jobs.Add(new Job { EpisodeId = episode.Id, Model = "m", QueuedBy = created.Username });
        await db.SaveChangesAsync();

        Assert.True((await store.DeleteAsync(created.Id)).Success);

        // Attribution is a record of what happened, so it outlives the account.
        Assert.Empty(db.Users);
        Assert.Equal("brian", (await db.Jobs.SingleAsync()).QueuedBy);
    }

    [Fact]
    public async Task Acting_on_an_account_that_is_gone_is_refused_rather_than_throwing()
    {
        await using var db = new AppDbContext(_dbOptions);
        var store = Create(db);

        Assert.False((await store.UpdateAsync(404, null, UserRole.Admin, true)).Success);
        Assert.False((await store.SetPasswordAsync(404, "password123")).Success);
        Assert.False((await store.DeleteAsync(404)).Success);
    }
}
