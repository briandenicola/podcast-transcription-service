namespace PodcastTranscription.Web.Domain;

/// <summary>
/// What an account is allowed to do. Ordered least to most, so a check can be a comparison
/// rather than a set of names, and stored as its integer value — renumbering would silently
/// promote or demote every account already in the database.
/// </summary>
public enum UserRole
{
    /// <summary>Reads the library: browse, search, play, read transcripts, export. Changes nothing.</summary>
    Viewer = 0,

    /// <summary>
    /// Everything a viewer can do, plus the work: add episodes, queue and cancel transcriptions,
    /// correct text, summarise, and manage feeds. Deliberately cannot delete anything or touch
    /// the app's own configuration.
    /// </summary>
    Member = 1,

    /// <summary>The lot, including deletion, retention, backups and other people's accounts.</summary>
    Admin = 2
}
