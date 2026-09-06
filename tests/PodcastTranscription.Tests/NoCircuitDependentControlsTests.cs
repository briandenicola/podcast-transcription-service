using System.Text.RegularExpressions;

namespace PodcastTranscription.Tests;

/// <summary>
/// The regression guard for the bug that has now bitten this app four times.
///
/// An <c>@onclick</c> or <c>@onchange</c> handler only runs over a live Blazor circuit. Where the
/// websocket does not connect — a reverse proxy that forwards HTTP but not the upgrade is all it
/// takes — the page renders perfectly and every one of those controls silently does nothing.
/// That is indistinguishable from a broken app, and it is invisible in development, where the
/// circuit always connects.
///
/// So the rule is: anything that changes something is a form post or a link. This test reads the
/// components and fails when a new one reaches for a circuit handler instead.
/// </summary>
public class NoCircuitDependentControlsTests
{
    private static readonly Regex Handler = new(@"@on(click|change|input|submit)\s*=", RegexOptions.Compiled);

    /// <summary>
    /// Pages that render statically by design and post ordinary forms. Login is the one place a
    /// component genuinely owns a form, because writing the auth cookie has to happen outside a
    /// circuit — which is the same reason everything else here does too.
    /// </summary>
    private static readonly string[] Allowed = ["Login.razor"];

    public static IEnumerable<object[]> Components()
    {
        var root = ComponentRoot();

        return Directory
            .EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Where(path => !Allowed.Contains(Path.GetFileName(path)))
            .Select(path => new object[] { Path.GetRelativePath(root, path) });
    }

    [Theory]
    [MemberData(nameof(Components))]
    public void No_component_drives_an_action_through_the_blazor_circuit(string relativePath)
    {
        var path = Path.Combine(ComponentRoot(), relativePath);
        var offending = File.ReadLines(path)
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(l => Handler.IsMatch(l.Line))
            .Select(l => $"  line {l.Number}: {l.Line.Trim()}")
            .ToList();

        Assert.True(offending.Count == 0,
            $"{relativePath} drives a control through the Blazor circuit, so it does nothing "
            + "wherever the websocket does not connect. Use a form post (see ActionEndpoints) or "
            + "a link instead:\n" + string.Join("\n", offending));
    }

    [Fact]
    public void The_guard_is_actually_looking_at_the_components()
    {
        var found = Components().Count();

        // A wrong path would make every test above pass by finding nothing at all.
        Assert.True(found > 10, $"Expected to find the components; found {found}.");
    }

    /// <summary>
    /// Walks up to the repository root rather than assuming a build layout, so this keeps working
    /// from the IDE, from `dotnet test`, and from CI.
    /// </summary>
    private static string ComponentRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "PodcastTranscription.Web", "Components");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the Components directory.");
    }
}
