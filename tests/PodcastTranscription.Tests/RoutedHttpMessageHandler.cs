using System.Net;

namespace PodcastTranscription.Tests;

/// <summary>
/// Answers by path rather than in order, which is what an Ollama conversation needs: the health
/// check hits <c>/api/tags</c> and every pass hits <c>/api/generate</c>, and the interesting
/// assertions are about how many of the latter there were.
/// </summary>
public class RoutedHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _routes = new();
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _defaults = new();

    public List<(string Path, string Body)> Requests { get; } = [];

    public int CountFor(string path) =>
        Requests.Count(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Always answers this path with this response.</summary>
    public RoutedHttpMessageHandler Always(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _defaults[path] = (status, body);
        return this;
    }

    /// <summary>Answers this path with each response in turn, repeating the last one after that.</summary>
    public RoutedHttpMessageHandler Sequence(string path, params string[] bodies)
    {
        _routes[path] = new Queue<(HttpStatusCode, string)>(
            bodies.Select(b => (HttpStatusCode.OK, b)));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add((path, body));

        if (_routes.TryGetValue(path, out var queue) && queue.Count > 0)
        {
            var next = queue.Dequeue();
            return Respond(next.Status, next.Body);
        }

        if (_defaults.TryGetValue(path, out var fallback))
        {
            return Respond(fallback.Status, fallback.Body);
        }

        return Respond(HttpStatusCode.NotFound, """{"error":"no route"}""");
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
