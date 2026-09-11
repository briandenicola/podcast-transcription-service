using System.Net;

namespace PodcastTranscription.Tests;

/// <summary>Hands out clients backed by a canned response, for feed fetches.</summary>
public class FakeHttpClientFactory(Func<string> body, HttpStatusCode status = HttpStatusCode.OK) : IHttpClientFactory
{
    public List<string> RequestedUrls { get; } = [];

    /// <summary>The request body of each call, read as text — form-encoded posts included.</summary>
    public List<string> RequestedBodies { get; } = [];

    public HttpClient CreateClient(string name) => new(new Handler(this, body, status));

    private sealed class Handler(FakeHttpClientFactory owner, Func<string> body, HttpStatusCode status)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            owner.RequestedUrls.Add(request.RequestUri!.ToString());
            owner.RequestedBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body(), System.Text.Encoding.UTF8, "application/rss+xml")
            };
        }
    }
}
