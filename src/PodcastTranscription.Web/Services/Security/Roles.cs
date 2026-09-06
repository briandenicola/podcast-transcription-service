using PodcastTranscription.Web.Domain;

namespace PodcastTranscription.Web.Services.Security;

/// <summary>
/// Role and policy names in one place, so a typo in a string is a compile error somewhere rather
/// than an endpoint that quietly lets everybody through.
/// </summary>
public static class Roles
{
    public const string Admin = nameof(UserRole.Admin);
    public const string Member = nameof(UserRole.Member);
    public const string Viewer = nameof(UserRole.Viewer);

    /// <summary>Deletion, retention, backups, configuration and other people's accounts.</summary>
    public const string AdminPolicy = "RequireAdmin";

    /// <summary>
    /// Anything that changes the library without destroying part of it: adding episodes, queuing
    /// transcriptions, correcting text, summarising, managing feeds. Admins satisfy it too.
    /// </summary>
    public const string MemberPolicy = "RequireMember";

    public static string Name(UserRole role) => role.ToString();

    /// <summary>Roles that satisfy <see cref="MemberPolicy"/>.</summary>
    public static readonly string[] MemberOrAbove = [Member, Admin];
}
