using System.Net;

namespace PodcastTranscription.Tests;

/// <summary>Hands out clients backed by a canned response, for feed fetches.</summary>
public class FakeHttpClientFactory(Func<string> body, HttpStatusCode status = HttpStatusCode.OK) : IHttpClientFactory
{
    public List<string> RequestedUrls { get; } = [];

    public HttpClient CreateClient(string name) => new(new Handler(this, body, status));

    private sealed class Handler(FakeHttpClientFactory owner, Func<string> body, HttpStatusCode status)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            owner.RequestedUrls.Add(request.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body(), System.Text.Encoding.UTF8, "application/rss+xml")
            });
        }
    }
}
