using PodcastTranscription.Web.Services.Security;

namespace PodcastTranscription.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void A_password_verifies_against_its_own_hash()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void A_wrong_password_does_not_verify() =>
        Assert.False(PasswordHasher.Verify("wrong", PasswordHasher.Hash("right")));

    [Fact]
    public void Verification_is_case_sensitive() =>
        Assert.False(PasswordHasher.Verify("Secret", PasswordHasher.Hash("secret")));

    [Fact]
    public void The_same_password_hashes_differently_each_time()
    {
        // A random salt per hash, so identical passwords are not identifiable from the hashes.
        var first = PasswordHasher.Hash("same");
        var second = PasswordHasher.Hash("same");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("same", first));
        Assert.True(PasswordHasher.Verify("same", second));
    }

    [Fact]
    public void The_hash_records_its_algorithm_and_iterations()
    {
        var hash = PasswordHasher.Hash("x");

        Assert.StartsWith("pbkdf2$sha256$210000$", hash);
        Assert.Equal(5, hash.Split('$').Length);
    }

    /// <summary>
    /// A malformed hash must fail closed. A config value that has been truncated or mangled
    /// cannot be allowed to become a way past the check.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2$sha256$210000$onlytwoparts")]
    [InlineData("pbkdf2$sha256$0$AAAA$AAAA")]
    [InlineData("pbkdf2$sha256$-1$AAAA$AAAA")]
    [InlineData("pbkdf2$sha256$210000$not base64!$AAAA")]
    [InlineData("pbkdf2$sha256$210000$$")]
    [InlineData("bcrypt$12$whatever")]
    public void A_malformed_hash_never_verifies(string? stored) =>
        Assert.False(PasswordHasher.Verify("anything", stored));

    [Fact]
    public void An_empty_password_is_still_hashed_and_verified()
    {
        var hash = PasswordHasher.Hash(string.Empty);

        Assert.True(PasswordHasher.Verify(string.Empty, hash));
        Assert.False(PasswordHasher.Verify("x", hash));
    }

    [Fact]
    public void Unicode_passwords_round_trip()
    {
        var hash = PasswordHasher.Hash("pässwörd–π–🎧");

        Assert.True(PasswordHasher.Verify("pässwörd–π–🎧", hash));
    }

    [Theory]
    [InlineData("key", "key", true)]
    [InlineData("key", "keys", false)]
    [InlineData("key", "KEY", false)]
    [InlineData("", "", true)]
    [InlineData(null, "key", false)]
    [InlineData("key", null, false)]
    [InlineData(null, null, false)]
    public void Secret_comparison_matches_only_identical_values(string? a, string? b, bool expected) =>
        Assert.Equal(expected, PasswordHasher.SecretEquals(a, b));
}
