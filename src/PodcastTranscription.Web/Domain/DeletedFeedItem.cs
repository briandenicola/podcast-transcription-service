namespace PodcastTranscription.Web.Domain;

/// <summary>
/// A tombstone for an episode that was deleted after being recorded from a feed. Without this,
/// the next poll would see the feed's guid as unseen and quietly re-add the episode it was just
/// asked to forget.
/// </summary>
public class DeletedFeedItem
{
    public int Id { get; set; }
    public int FeedId { get; set; }
    public string FeedItemGuid { get; set; } = string.Empty;
    public DateTimeOffset DeletedAt { get; set; } = DateTimeOffset.UtcNow;
}
