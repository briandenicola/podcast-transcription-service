using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Components;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Data;
using PodcastTranscription.Web.Domain;
using PodcastTranscription.Web.Endpoints;
using PodcastTranscription.Web.Services;
using PodcastTranscription.Web.Services.Security;

namespace PodcastTranscription.Tests;

public class ApiKeyTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private WebApplication _app = default!;
    private HttpClient _client = default!;
    private string _mediaPath = string.Empty;
    private int _memberId;
    private int _viewerId;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(App).Assembly.FullName
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        builder.Services.Configure<AuthOptions>(o =>
        {
            o.Username = "configured-admin";
            o.PasswordHash = PasswordHasher.Hash("admin-password");
            o.ApiKey = "legacy-shared-key";
        });
        _mediaPath = Path.Combine(Path.GetTempPath(), $"podcast-api-tests-{Guid.NewGuid():N}");
        builder.Services.Configure<StorageOptions>(o => o.MediaPath = _mediaPath);
        builder.Services.AddSingleton<AdminAuthenticator>();
        builder.Services.AddApiKeyAccess();
        builder.Services.AddAuthentication(AdminAuthenticator.CookieScheme)
            .AddCookie(AdminAuthenticator.CookieScheme, o => o.Cookie.Name = "podcast-transcription.auth");
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Roles.AdminPolicy, p => p.RequireRole(Roles.Admin))
            .AddPolicy(Roles.MemberPolicy, p => p.RequireRole(Roles.MemberOrAbove));
        builder.Services.AddAntiforgery();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddScoped<EpisodeImporter>();
        builder.Services.AddScoped<MediaStore>();
        builder.Services.AddScoped<AudioProcessor>();
        builder.Services.AddScoped<JobQueue>();
        builder.Services.AddSingleton<RunningJobs>();
        builder.Services.AddSingleton<JobNotifier>();
        builder.Services.AddHttpClient<WhisperClient>();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseAntiforgery();
        _app.MapEpisodeApiEndpoints();
        _app.MapApiKeyEndpoints();
        _app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        _app.MapGet("/test-login/{id:int}", async (int id, HttpContext context, AppDbContext db,
            AdminAuthenticator admin) =>
        {
            var principal = id == 0 ? admin.CreatePrincipal()
                : SignInService.CreatePrincipal(await db.Users.SingleAsync(u => u.Id == id));
            await context.SignInAsync(AdminAuthenticator.CookieScheme, principal);
            return Results.Ok();
        });
        await _app.StartAsync();
        _client = _app.GetTestClient();

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        var member = new AppUser { Username = "member", PasswordHash = "unused", Role = UserRole.Member };
        var viewer = new AppUser { Username = "viewer", PasswordHash = "unused", Role = UserRole.Viewer };
        db.Users.AddRange(member, viewer);
        db.Episodes.Add(new Episode { Title = "Existing episode" });
        await db.SaveChangesAsync();
        _memberId = member.Id;
        _viewerId = viewer.Id;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
        if (Directory.Exists(_mediaPath))
        {
            Directory.Delete(_mediaPath);
        }
    }

    private async Task<T> WithStore<T>(Func<ApiKeyStore, AppDbContext, Task<T>> action)
    {
        using var scope = _app.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ApiKeyStore>(),
            scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<ClaimsPrincipal> Principal(int id) => WithStore(async (_, db) =>
        SignInService.CreatePrincipal(await db.Users.SingleAsync(u => u.Id == id)));

    private async Task<string> CreateKey(int id, bool submit = false)
    {
        var principal = await Principal(id);
        var created = await WithStore((keys, _) => keys.CreateAsync(principal, "Test integration", submit));
        return Assert.IsType<string>(created.Secret);
    }

    private Task<HttpResponseMessage> Read(string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/episodes/1");
        if (key is not null)
        {
            request.Headers.Add("X-API-Key", key);
        }
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Submit(string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/episodes")
        {
            Content = JsonContent.Create(new { url = "https://example.com/episode.mp3", show = "Test show" })
        };
        request.Headers.Add("X-API-Key", key);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Keys_are_random_and_only_the_hash_is_stored()
    {
        var first = await CreateKey(_memberId);
        var second = await CreateKey(_memberId);
        Assert.Matches("^pt_[a-f0-9]{64}$", first);
        Assert.NotEqual(first, second);
        await WithStore(async (_, db) =>
        {
            var stored = await db.ApiKeys.OrderBy(k => k.Id).FirstAsync();
            Assert.DoesNotContain(first, stored.KeyHash);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(first))), stored.KeyHash);
            Assert.Equal(first[..11], stored.Prefix);
            Assert.Equal(_memberId, stored.UserId);
            Assert.Null(stored.ConfiguredUsername);
            return true;
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-key")]
    public async Task Missing_or_invalid_keys_return_401_not_a_login_redirect(string? key)
    {
        var response = await Read(key);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Multiple_key_headers_are_rejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/episodes/1");
        request.Headers.Add("X-API-Key", new[] { await CreateKey(_memberId), "another-key" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Per_user_keys_work_without_a_shared_key_and_record_last_use()
    {
        using var scope = _app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value.ApiKey = null;
        var secret = await CreateKey(_viewerId);
        Assert.Equal(HttpStatusCode.OK, (await Read(secret)).StatusCode);
        await WithStore(async (_, db) =>
        {
            Assert.NotNull((await db.ApiKeys.SingleAsync()).LastUsedAt);
            return true;
        });
    }

    [Fact]
    public async Task Read_only_keys_cannot_submit_even_for_members()
    {
        var response = await Submit(await CreateKey(_memberId));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await WithStore(async (_, db) =>
        {
            Assert.Empty(db.Jobs);
            return true;
        });
    }

    [Fact]
    public async Task Submission_keys_queue_work_with_the_owners_name()
    {
        var response = await Submit(await CreateKey(_memberId, submit: true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith("/api/episodes/", response.Headers.Location?.OriginalString);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "episodeId", "jobId", "status", "url" },
            body.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("Queued", body.GetProperty("status").GetString());
        var status = await _client.SendAsync(StatusRequest(body.GetProperty("episodeId").GetInt32(),
            await CreateKey(_viewerId)));
        var detail = await status.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(body.GetProperty("jobId").GetInt32(),
            detail.GetProperty("latestJob").GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Array, detail.GetProperty("transcripts").ValueKind);
        await WithStore(async (_, db) =>
        {
            Assert.Equal("member", (await db.Jobs.SingleAsync()).QueuedBy);
            return true;
        });
    }

    [Fact]
    public async Task Shared_keys_keep_existing_read_and_submit_access_without_user_attribution()
    {
        Assert.Equal(HttpStatusCode.OK, (await Read("legacy-shared-key")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Submit("legacy-shared-key")).StatusCode);
        await WithStore(async (_, db) =>
        {
            Assert.Null((await db.Jobs.SingleAsync()).QueuedBy);
            return true;
        });
    }

    [Fact]
    public async Task Viewers_cannot_create_submission_keys()
    {
        var viewer = await Principal(_viewerId);
        var result = await WithStore((keys, _) => keys.CreateAsync(viewer, "Escalation", true));
        Assert.Null(result.Secret);
        Assert.Contains("Only Members and Admins", result.Message);
    }

    [Fact]
    public async Task A_role_downgrade_immediately_removes_submit_access_but_keeps_read()
    {
        var secret = await CreateKey(_memberId, submit: true);
        await WithStore(async (_, db) =>
        {
            (await db.Users.SingleAsync(u => u.Id == _memberId)).Role = UserRole.Viewer;
            await db.SaveChangesAsync();
            return true;
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await Submit(secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Read(secret)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authentication_limits_claims_to_the_key_permission_and_current_role(bool downgrade)
    {
        var secret = await CreateKey(_memberId, submit: downgrade);
        await WithStore(async (keys, db) =>
        {
            if (downgrade)
            {
                (await db.Users.SingleAsync(u => u.Id == _memberId)).Role = UserRole.Viewer;
                await db.SaveChangesAsync();
            }
            var principal = await keys.AuthenticateAsync(secret);
            Assert.NotNull(principal);
            Assert.True(principal.IsInRole(downgrade ? Roles.Viewer : Roles.Member));
            Assert.False(principal.HasClaim(ApiKeyAuthenticationHandler.PermissionClaim, "submit"));
            return true;
        });
    }

    [Fact]
    public async Task Submission_policy_also_requires_a_member_role()
    {
        using var scope = _app.Services.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, Roles.Viewer),
             new Claim(ApiKeyAuthenticationHandler.PermissionClaim, "read"),
             new Claim(ApiKeyAuthenticationHandler.PermissionClaim, "submit")],
            ApiKeyAuthenticationHandler.SchemeName));
        Assert.False((await authorization.AuthorizeAsync(principal, null,
            ApiKeyAuthenticationHandler.SubmitPolicy)).Succeeded);
    }

    [Fact]
    public async Task A_stale_cookie_cannot_create_a_submission_key_after_role_downgrade()
    {
        var stale = await Principal(_memberId);
        await WithStore(async (_, db) =>
        {
            (await db.Users.SingleAsync(u => u.Id == _memberId)).Role = UserRole.Viewer;
            await db.SaveChangesAsync();
            return true;
        });
        Assert.Null((await WithStore((keys, _) => keys.CreateAsync(stale, "Stale role", true))).Secret);
    }

    [Fact]
    public async Task Revocation_immediately_blocks_read_and_submit()
    {
        var secret = await CreateKey(_memberId, submit: true);
        var member = await Principal(_memberId);
        Assert.True(await WithStore(async (keys, db) =>
            await keys.RevokeAsync(member, (await db.ApiKeys.SingleAsync()).Id)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Read(secret)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Submit(secret)).StatusCode);
    }

    [Fact]
    public async Task Users_cannot_list_or_revoke_another_users_keys()
    {
        var secret = await CreateKey(_memberId);
        var viewer = await Principal(_viewerId);
        await WithStore(async (keys, db) =>
        {
            Assert.Empty(await keys.ListAsync(viewer));
            Assert.False(await keys.RevokeAsync(viewer, (await db.ApiKeys.SingleAsync()).Id));
            return true;
        });
        Assert.Equal(HttpStatusCode.OK, (await Read(secret)).StatusCode);
    }

    [Fact]
    public async Task Deactivation_blocks_existing_keys_and_stale_cookie_management()
    {
        var secret = await CreateKey(_memberId);
        var stale = await Principal(_memberId);
        await WithStore(async (_, db) =>
        {
            (await db.Users.SingleAsync(u => u.Id == _memberId)).IsActive = false;
            await db.SaveChangesAsync();
            return true;
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await Read(secret)).StatusCode);
        Assert.Null((await WithStore((keys, _) => keys.CreateAsync(stale, "Inactive", false))).Secret);
    }

    [Fact]
    public async Task Inactive_accounts_cannot_manage_keys_using_stale_cookies()
    {
        await CreateKey(_memberId);
        var stale = await Principal(_memberId);
        await WithStore(async (_, db) =>
        {
            (await db.Users.SingleAsync(u => u.Id == _memberId)).IsActive = false;
            await db.SaveChangesAsync();
            return true;
        });
        await WithStore(async (keys, db) =>
        {
            Assert.Null((await keys.CreateAsync(stale, "Inactive", false)).Secret);
            Assert.Empty(await keys.ListAsync(stale));
            Assert.False(await keys.RevokeAsync(stale, (await db.ApiKeys.SingleAsync()).Id));
            return true;
        });
    }

    [Fact]
    public async Task Deletion_cascades_keys_and_recreated_accounts_do_not_inherit_them()
    {
        var secret = await CreateKey(_memberId);
        await WithStore(async (_, db) =>
        {
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == _memberId));
            await db.SaveChangesAsync();
            db.Users.Add(new AppUser { Username = "member", PasswordHash = "unused", Role = UserRole.Member });
            await db.SaveChangesAsync();
            Assert.Empty(db.ApiKeys);
            return true;
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await Read(secret)).StatusCode);
    }

    [Fact]
    public async Task Configured_admin_can_manage_keys_without_a_database_account()
    {
        using var scope = _app.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<AdminAuthenticator>();
        var created = await WithStore((keys, _) => keys.CreateAsync(admin.CreatePrincipal(), "Admin", true));
        Assert.Equal(HttpStatusCode.Created, (await Submit(Assert.IsType<string>(created.Secret))).StatusCode);
        await WithStore(async (_, db) =>
        {
            var stored = await db.ApiKeys.SingleAsync();
            Assert.Null(stored.UserId);
            Assert.Equal(admin.Username, stored.ConfiguredUsername);
            Assert.Equal(admin.Username, (await db.Jobs.SingleAsync()).QueuedBy);
            return true;
        });
    }

    [Fact]
    public async Task Keys_for_a_different_configured_admin_are_rejected()
    {
        using var scope = _app.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<AdminAuthenticator>();
        var created = await WithStore((keys, _) => keys.CreateAsync(admin.CreatePrincipal(), "Admin", false));
        await WithStore(async (_, db) =>
        {
            (await db.ApiKeys.SingleAsync()).ConfiguredUsername = "previous-admin";
            await db.SaveChangesAsync();
            return true;
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await Read(created.Secret)).StatusCode);
    }

    [Fact]
    public async Task Configured_admin_keys_require_a_configured_password()
    {
        using var scope = _app.Services.CreateScope();
        var principal = scope.ServiceProvider.GetRequiredService<AdminAuthenticator>().CreatePrincipal();
        var created = await WithStore((keys, _) => keys.CreateAsync(principal, "Admin", false));
        await WithStore(async (_, db) =>
        {
            var noPassword = new AdminAuthenticator(Options.Create(new AuthOptions
            {
                Username = "configured-admin"
            }), NullLogger<AdminAuthenticator>.Instance);
            var keys = new ApiKeyStore(db, noPassword, NullLogger<ApiKeyStore>.Instance);
            Assert.Null(await keys.AuthenticateAsync(Assert.IsType<string>(created.Secret)));
            Assert.Null((await keys.CreateAsync(principal, "No password", false)).Secret);
            return true;
        });
    }

    [Fact]
    public async Task Only_authenticated_cookie_principals_can_manage_keys()
    {
        var real = await Principal(_memberId);
        var api = new ClaimsPrincipal(new ClaimsIdentity(real.Claims, ApiKeyAuthenticationHandler.SchemeName));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(real.Claims));
        Assert.Null((await WithStore((keys, _) => keys.CreateAsync(api, "API", false))).Secret);
        Assert.Null((await WithStore((keys, _) => keys.CreateAsync(anonymous, "Anonymous", false))).Secret);
    }

    [Fact]
    public async Task Cookies_cannot_replace_api_keys()
    {
        await SignIn(_memberId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Read(null)).StatusCode);
    }

    [Fact]
    public async Task Api_keys_cannot_manage_keys_through_cookie_endpoints()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api-keys/1/revoke");
        request.Headers.Add("X-API-Key", await CreateKey(_memberId, submit: true));
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Key_names_are_required_and_bounded()
    {
        var member = await Principal(_memberId);
        Assert.Null((await WithStore((keys, _) => keys.CreateAsync(member, " ", false))).Secret);
        Assert.Null((await WithStore((keys, _) => keys.CreateAsync(member, new string('x', 101), false))).Secret);
    }

    [Fact]
    public async Task Revoke_forms_require_an_antiforgery_token()
    {
        await CreateKey(_memberId);
        await SignIn(_memberId);
        var response = await _client.PostAsync("/api-keys/1/revoke", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await WithStore(async (_, db) =>
        {
            Assert.Null((await db.ApiKeys.SingleAsync()).RevokedAt);
            return true;
        });
    }

    [Fact]
    public async Task Create_forms_require_an_antiforgery_token()
    {
        await SignIn(_memberId);
        var response = await _client.PostAsync("/api-keys", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "create-api-key",
            ["Input.Name"] = "Missing token"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await WithStore(async (_, db) =>
        {
            Assert.Empty(db.ApiKeys);
            return true;
        });
    }

    [Fact]
    public async Task Revoking_a_missing_key_fails_and_repeated_revocation_is_safe()
    {
        await CreateKey(_memberId);
        var member = await Principal(_memberId);
        await WithStore(async (keys, db) =>
        {
            Assert.False(await keys.RevokeAsync(member, int.MaxValue));
            var id = (await db.ApiKeys.SingleAsync()).Id;
            Assert.True(await keys.RevokeAsync(member, id));
            var revokedAt = (await db.ApiKeys.SingleAsync()).RevokedAt;
            Assert.True(await keys.RevokeAsync(member, id));
            Assert.Equal(revokedAt, (await db.ApiKeys.SingleAsync()).RevokedAt);
            return true;
        });
    }

    [Fact]
    public async Task Invalid_submission_urls_still_return_validation_errors_and_missing_episodes_return_404()
    {
        var secret = await CreateKey(_memberId, submit: true);
        foreach (var url in new[] { "", "file:///audio.mp3" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/episodes")
            {
                Content = JsonContent.Create(new { url })
            };
            request.Headers.Add("X-API-Key", secret);
            var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("errors").TryGetProperty("url", out _));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(StatusRequest(int.MaxValue, secret))).StatusCode);
    }

    [Fact]
    public async Task Migration_preserves_existing_library_and_accounts_and_matches_the_model()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync("20260911135320_DeletedFeedItems");
        db.Users.Add(new AppUser { Username = "existing", PasswordHash = "unchanged" });
        db.Episodes.Add(new Episode { Title = "Preserved episode" });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        Assert.Equal("unchanged", (await db.Users.SingleAsync()).PasswordHash);
        Assert.Equal("Preserved episode", (await db.Episodes.SingleAsync()).Title);
        Assert.Empty(await db.ApiKeys.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Ui_creates_displays_once_and_revokes_without_a_blazor_circuit()
    {
        await SignIn(_memberId);
        var page = await _client.GetAsync("/api-keys");
        Assert.True(page.StatusCode == HttpStatusCode.OK, await page.Content.ReadAsStringAsync());
        Assert.True(page.Headers.CacheControl?.NoStore);
        var html = await page.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<!--Blazor:", html);
        var token = AntiforgeryToken(html);
        SetAntiforgeryCookie(page);
        var created = await _client.PostAsync("/api-keys", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "create-api-key",
            ["__RequestVerificationToken"] = token,
            ["Input.Name"] = "Browser integration",
            ["Input.CanSubmit"] = "true"
        }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.True(created.Headers.CacheControl?.NoStore);
        var createdHtml = await created.Content.ReadAsStringAsync();
        var secret = Regex.Match(createdHtml, @"pt_[a-f0-9]{64}").Value;
        Assert.NotEmpty(secret);
        var reloaded = await _client.GetStringAsync("/api-keys");
        Assert.DoesNotContain(secret, reloaded);
        Assert.Contains("Browser integration", reloaded);
        var revoked = await _client.PostAsync("/api-keys/1/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AntiforgeryToken(reloaded)
            }));
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        Assert.Equal("/api-keys?revoked=true", revoked.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Read(secret)).StatusCode);
    }

    private async Task SignIn(int id)
    {
        var response = await _client.GetAsync($"/test-login/{id}");
        var cookie = response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("podcast", StringComparison.Ordinal)
            || v.StartsWith(".AspNetCore.Cookies", StringComparison.Ordinal));
        _client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
    }

    private static HttpRequestMessage StatusRequest(int id, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/episodes/{id}");
        request.Headers.Add("X-API-Key", secret);
        return request;
    }

    private void SetAntiforgeryCookie(HttpResponseMessage response)
    {
        foreach (var cookie in response.Headers.GetValues("Set-Cookie"))
        {
            if (cookie.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal))
            {
                _client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
            }
        }
    }

    private static string AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Expected an antiforgery token in the form.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
