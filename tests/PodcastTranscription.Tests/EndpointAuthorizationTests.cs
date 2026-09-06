using System.Text.RegularExpressions;

namespace PodcastTranscription.Tests;

/// <summary>
/// Every endpoint that changes something has to say which role it needs.
///
/// Hiding a button from a viewer is a courtesy, not a rule — the URL is still there, and a
/// state-changing POST that forgets its policy is protected only by the fallback, which any
/// signed-in account satisfies including a read-only one. This reads the endpoint files and
/// fails when a new POST does not declare a policy, because that mistake is invisible until
/// someone with a viewer account deletes an episode.
/// </summary>
public class EndpointAuthorizationTests
{
    private static readonly Regex MapPost = new(@"app\.MapPost\(""(?<route>[^""]+)""", RegexOptions.Compiled);

    /// <summary>
    /// Sign-out is the one POST that must stay open to anybody signed in, whatever their role —
    /// including an account that has just been deactivated.
    /// </summary>
    private static readonly string[] Exempt = ["/logout"];

    public static IEnumerable<object[]> Endpoints()
    {
        foreach (var file in Directory.EnumerateFiles(EndpointRoot(), "*.cs"))
        {
            var source = File.ReadAllText(file);

            foreach (Match match in MapPost.Matches(source))
            {
                var route = match.Groups["route"].Value;
                if (!Exempt.Contains(route))
                {
                    yield return [Path.GetFileName(file), route];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public void Every_state_changing_endpoint_declares_the_role_it_needs(string fileName, string route)
    {
        var source = File.ReadAllText(Path.Combine(EndpointRoot(), fileName));

        // Take the text from this route's registration to the next one, which is where its own
        // .RequireAuthorization(...) has to be.
        var start = source.IndexOf($@"app.MapPost(""{route}""", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {route} in {fileName}.");

        var next = MapPost.Match(source, start + 1);
        var block = next.Success ? source[start..next.Index] : source[start..];

        Assert.True(
            block.Contains("RequireAuthorization(", StringComparison.Ordinal),
            $"{fileName}: POST {route} does not declare a policy, so any signed-in account can "
            + "call it — including a read-only viewer. Add .RequireAuthorization(Roles.AdminPolicy) "
            + "or .RequireAuthorization(Roles.MemberPolicy).");
    }

    [Fact]
    public void Deleting_anything_needs_an_admin()
    {
        var source = File.ReadAllText(Path.Combine(EndpointRoot(), "DeletionEndpoints.cs"));

        foreach (Match match in MapPost.Matches(source))
        {
            var start = match.Index;
            var next = MapPost.Match(source, start + 1);
            var block = next.Success ? source[start..next.Index] : source[start..];

            Assert.True(
                block.Contains("Roles.AdminPolicy", StringComparison.Ordinal),
                $"POST {match.Groups["route"].Value} deletes something, so it must be admin-only.");
        }
    }

    [Fact]
    public void The_guard_is_actually_reading_the_endpoints()
    {
        var found = Endpoints().Count();

        // A wrong path would make every test above pass by finding nothing at all.
        Assert.True(found > 15, $"Expected to find the endpoints; found {found}.");
    }

    private static string EndpointRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "PodcastTranscription.Web", "Endpoints");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the Endpoints directory.");
    }
}
