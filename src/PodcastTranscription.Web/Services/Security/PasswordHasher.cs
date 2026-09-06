using System.Security.Cryptography;
using System.Text;

namespace PodcastTranscription.Web.Services.Security;

/// <summary>
/// PBKDF2-SHA256 password hashing, in the format
/// <c>pbkdf2$sha256$iterations$saltBase64$hashBase64</c>.
///
/// PBKDF2 rather than bcrypt or Argon2 because it is in the framework: one fewer dependency for
/// a single-admin login, and it is what ASP.NET Identity uses.
/// </summary>
public static class PasswordHasher
{
    /// <summary>OWASP's floor for PBKDF2-SHA256 at time of writing.</summary>
    private const int Iterations = 210_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Prefix = "pbkdf2$sha256$";

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verifies a password against a stored hash. Returns false rather than throwing on a
    /// malformed hash: a broken config value must not be a way past the check.
    /// </summary>
    public static bool Verify(string password, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash) || !storedHash.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = storedHash[Prefix.Length..].Split('$');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var iterations)
            || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // Constant time, so a wrong password cannot be narrowed down by how long the check took.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Constant-time comparison for the API key, which is a shared secret rather than a hash.</summary>
    public static bool SecretEquals(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }
}
