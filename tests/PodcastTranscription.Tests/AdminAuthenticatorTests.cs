using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastTranscription.Web.Configuration;
using PodcastTranscription.Web.Services.Security;

namespace PodcastTranscription.Tests;

public class AdminAuthenticatorTests
{
    private static AdminAuthenticator Create(AuthOptions options) =>
        new(Options.Create(options), NullLogger<AdminAuthenticator>.Instance);

    [Fact]
    public void A_configured_hash_authenticates_the_admin()
    {
        var admin = Create(new AuthOptions
        {
            Username = "brian",
            PasswordHash = PasswordHasher.Hash("s3cret")
        });

        Assert.True(admin.HasPassword);
        Assert.True(admin.ValidateCredentials("brian", "s3cret"));
    }

    [Fact]
    public void A_plaintext_password_is_hashed_on_load_and_still_works()
    {
        var admin = Create(new AuthOptions { Username = "brian", Password = "s3cret" });

        Assert.True(admin.ValidateCredentials("brian", "s3cret"));
        Assert.False(admin.ValidateCredentials("brian", "wrong"));
    }

    [Fact]
    public void The_hash_wins_when_both_are_configured()
    {
        var admin = Create(new AuthOptions
        {
            Username = "brian",
            PasswordHash = PasswordHasher.Hash("from-hash"),
            Password = "from-plaintext"
        });

        Assert.True(admin.ValidateCredentials("brian", "from-hash"));
        Assert.False(admin.ValidateCredentials("brian", "from-plaintext"));
    }

    [Fact]
    public void The_wrong_username_is_rejected_even_with_the_right_password()
    {
        var admin = Create(new AuthOptions { Username = "brian", Password = "s3cret" });

        Assert.False(admin.ValidateCredentials("someone-else", "s3cret"));
    }

    /// <summary>With no password configured nothing authenticates — the startup guard refuses to
    /// run in this state, and this is the second line of defence.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("anything")]
    public void Without_a_configured_password_nothing_authenticates(string? attempt)
    {
        var admin = Create(new AuthOptions { Username = "brian" });

        Assert.False(admin.HasPassword);
        Assert.False(admin.ValidateCredentials("brian", attempt));
    }

    [Fact]
    public void The_api_is_disabled_until_a_key_is_configured()
    {
        var admin = Create(new AuthOptions { Password = "x" });

        Assert.False(admin.ApiEnabled);

        // Crucially, no key configured must not mean any key is accepted.
        Assert.False(admin.ValidateApiKey("guessed"));
        Assert.False(admin.ValidateApiKey(null));
        Assert.False(admin.ValidateApiKey(string.Empty));
    }

    [Fact]
    public void A_configured_api_key_matches_only_itself()
    {
        var admin = Create(new AuthOptions { Password = "x", ApiKey = "abc123" });

        Assert.True(admin.ApiEnabled);
        Assert.True(admin.ValidateApiKey("abc123"));
        Assert.False(admin.ValidateApiKey("abc124"));
        Assert.False(admin.ValidateApiKey("abc123 "));
        Assert.False(admin.ValidateApiKey(null));
    }

    [Fact]
    public void The_principal_carries_the_configured_username_and_the_admin_role()
    {
        var principal = Create(new AuthOptions { Username = "brian", Password = "x" }).CreatePrincipal();

        Assert.Equal("brian", principal.Identity!.Name);
        Assert.True(principal.IsInRole("Admin"));
        Assert.True(principal.Identity.IsAuthenticated);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(30, 30)]
    [InlineData(10_000, 365)]
    public void The_session_length_is_clamped_to_something_sensible(int configured, int expectedDays)
    {
        var admin = Create(new AuthOptions { Password = "x", SessionDays = configured });

        Assert.Equal(TimeSpan.FromDays(expectedDays), admin.SessionLength);
    }
}
