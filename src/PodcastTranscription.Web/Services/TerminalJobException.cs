namespace PodcastTranscription.Web.Services;

/// <summary>
/// A failure that retrying cannot fix — a duplicate download, a URL that is not http(s), audio
/// that contains nothing. The worker fails these immediately rather than burning the attempt
/// budget on an outcome that will not change.
/// </summary>
public class TerminalJobException(string message) : Exception(message);
