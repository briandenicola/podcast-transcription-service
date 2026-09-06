using System.Text.Json;

namespace PodcastTranscription.Tests;

public class PwaAssetsTests
{
    [Fact]
    public void Manifest_describes_an_installable_standalone_app()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(WebRoot("manifest.webmanifest")));
        var root = manifest.RootElement;

        Assert.Equal("Podcast Transcription", root.GetProperty("name").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        Assert.Equal("./", root.GetProperty("start_url").GetString());

        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        Assert.Contains(icons, icon => icon.GetProperty("sizes").GetString() == "192x192");
        Assert.Contains(icons, icon => icon.GetProperty("sizes").GetString() == "512x512");

        foreach (var icon in icons)
        {
            Assert.True(File.Exists(WebRoot(icon.GetProperty("src").GetString()!)));
        }
    }

    [Fact]
    public void App_registers_the_manifest_theme_and_service_worker()
    {
        var app = File.ReadAllText(Component("App.razor"));

        Assert.Contains("rel=\"manifest\"", app);
        Assert.Contains("name=\"theme-color\"", app);
        Assert.Contains("js/pwa.js", app);
        Assert.Contains("wp7.css", app);

        var registration = File.ReadAllText(WebRoot("js", "pwa.js"));
        Assert.Contains("serviceWorker.register", registration);
    }

    [Fact]
    public void Service_worker_caches_no_authenticated_content()
    {
        var worker = File.ReadAllText(WebRoot("service-worker.js"));

        Assert.Contains("request.mode === 'navigate'", worker);
        Assert.Contains("fetch(request).catch", worker);
        Assert.DoesNotContain("caches.put", worker);
        Assert.DoesNotContain("/episodes/", worker);
        Assert.DoesNotContain("/media/", worker);
    }

    [Fact]
    public void Metro_reader_is_limited_to_installed_display_mode()
    {
        var theme = File.ReadAllText(WebRoot("wp7.css"));

        Assert.StartsWith("/*", theme);
        Assert.Contains("@media (display-mode: standalone)", theme);
        Assert.Contains(".summary-content", theme);
        Assert.Contains(".transcript-line", theme);
        Assert.Contains(".episode-player", theme);
    }

    private static string Component(params string[] parts) =>
        Path.Combine(RepositoryRoot(), "src", "PodcastTranscription.Web", "Components",
            Path.Combine(parts));

    private static string WebRoot(params string[] parts) =>
        Path.Combine(RepositoryRoot(), "src", "PodcastTranscription.Web", "wwwroot",
            Path.Combine(parts));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PodcastTranscription.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
