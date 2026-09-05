using System.Net;

namespace PodcastTranscription.Tests;

/// <summary>
/// Answers each request with the next canned response, so a multi-chunk job can be given a
/// different transcript per chunk. Records every request for later assertions.
/// </summary>
public class SequencedHttpMessageHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
{
    private int _index;

    public List<string> RequestBodies { get; } = [];
    public int RequestCount => RequestBodies.Count;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));

        var response = responses[Math.Min(_index++, responses.Length - 1)];
        return new HttpResponseMessage(response.Status)
        {
            Content = new StringContent(response.Body, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
