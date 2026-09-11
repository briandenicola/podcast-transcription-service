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

    [Fact]
    public void Pwa_library_uses_cards_and_reader_links_target_the_current_episode()
    {
        var home = File.ReadAllText(Component("Pages", "Home.razor"));
        var episode = File.ReadAllText(Component("Pages", "EpisodeDetail.razor"));
        var theme = File.ReadAllText(WebRoot("wp7.css"));

        Assert.Contains("library-filter-popout", home);
        Assert.Contains("library-add-episode", home);
        Assert.Contains("pwa-library-list", home);
        Assert.Contains("pwa-library-card", home);
        Assert.Matches(@"\.library-add-episode\s*\{\s*display:\s*none;", theme);
        Assert.Contains(".library-table { display: none; }", theme);
        Assert.Contains("overflow-x: hidden", theme);

        Assert.Contains("/episodes/{EpisodeId}{ReaderQuery}#summary-card", episode);
        Assert.Contains("/episodes/{EpisodeId}{ReaderQuery}#transcript-body", episode);
        Assert.Contains("<h2 class=\"transcript-section-title\">Transcription</h2>", episode);
        Assert.Matches(@"\.transcript-section-title\s*\{[^}]*display:\s*block;", theme);
        Assert.DoesNotContain("href=\"#summary-card\"", episode);
        Assert.DoesNotContain("href=\"#transcript-body\"", episode);
    }

    [Fact]
    public void Desktop_library_supports_bulk_actions_and_preserves_filters_when_deleting()
    {
        var home = File.ReadAllText(Component("Pages", "Home.razor"));
        var confirmation = File.ReadAllText(Component("Pages", "ConfirmDelete.razor"));
        var actions = File.ReadAllText(Source("Endpoints", "ActionEndpoints.cs"));
        var deletion = File.ReadAllText(Source("Endpoints", "DeletionEndpoints.cs"));

        Assert.Contains("name=\"episodeIds\"", home);
        Assert.Contains("value=\"queue\"", home);
        Assert.Contains("value=\"delete\"", home);
        Assert.Contains("Selected episodes:", home);
        Assert.Contains("name=\"returnUrl\" value=\"@PageLink(_page)\"", home);
        Assert.Contains("@page \"/delete/episodes\"", confirmation);
        Assert.Contains("app.MapPost(\"/episodes/bulk\"", actions);
        Assert.Contains("app.MapPost(\"/delete/episodes/execute\"", deletion);
        Assert.Contains("SafeLibraryReturnUrl", deletion);
    }

    [Fact]
    public void Pwa_episode_actions_use_a_title_menu_and_a_large_empty_state_transcribe_action()
    {
        var episode = File.ReadAllText(Component("Pages", "EpisodeDetail.razor"));
        var desktopTheme = File.ReadAllText(WebRoot("app.css"));
        var pwaTheme = File.ReadAllText(WebRoot("wp7.css"));

        Assert.Contains("<details class=\"pwa-episode-menu\">", episode);
        Assert.Contains("aria-label=\"Open episode actions\"", episode);
        Assert.Contains("class=\"pwa-menu-transcribe\"", episode);
        Assert.Contains("class=\"pwa-menu-action-row\"", episode);
        Assert.Contains("class=\"pwa-transcribe-cta\"", episode);
        Assert.Contains(".pwa-episode-menu,", desktopTheme);
        Assert.Contains(".episode-command-header { display: none; }", pwaTheme);
        Assert.Matches(@"\.reader-tools\s*\{\s*display:\s*none\s*!important;", pwaTheme);
        Assert.Matches(@"\.pwa-transcribe-cta\s*\{\s*display:\s*grid;", pwaTheme);
    }

    [Fact]
    public void Transcription_forms_cannot_override_the_configured_whisper_model()
    {
        var episode = File.ReadAllText(Component("Pages", "EpisodeDetail.razor"));
        var feed = File.ReadAllText(Component("Pages", "FeedDetail.razor"));
        var actions = File.ReadAllText(Source("Endpoints", "ActionEndpoints.cs"));
        var queue = File.ReadAllText(Source("Services", "JobQueue.cs"));

        Assert.DoesNotContain("name=\"model\"", episode);
        Assert.DoesNotContain("name=\"model\"", feed);
        Assert.DoesNotContain("form[\"model\"]", actions);
        Assert.DoesNotContain("DefaultModel", queue);
        Assert.Contains("Model = whisper.Model", queue);
    }

    [Fact]
    public void Pwa_jobs_use_filterable_operational_cards_instead_of_the_desktop_table()
    {
        var jobs = File.ReadAllText(Component("Pages", "Jobs.razor"));
        var desktopTheme = File.ReadAllText(WebRoot("app.css"));
        var pwaTheme = File.ReadAllText(WebRoot("wp7.css"));

        Assert.Contains("<th>Requested by</th>", jobs);
        Assert.Contains("jobs-filter-popout", jobs);
        Assert.Contains("pwa-job-list", jobs);
        Assert.Contains("pwa-job-card", jobs);
        Assert.Contains("pwa-job-progress", jobs);
        Assert.Contains("pwa-job-error", jobs);
        Assert.Contains("name=\"state\"", jobs);
        Assert.Contains("name=\"sort\"", jobs);
        Assert.Contains("OnParametersSetAsync", jobs);
        Assert.Contains(".pwa-job-list,", desktopTheme);
        Assert.Matches(@"\.jobs-table\s*\{\s*display:\s*none;", pwaTheme);
        Assert.Matches(@"\.pwa-job-list\s*\{\s*display:\s*block;", pwaTheme);
    }

    [Fact]
    public void Pwa_settings_lives_in_the_admin_overflow_instead_of_the_title_bar()
    {
        var layout = File.ReadAllText(Component("Layout", "MainLayout.razor"));
        var layoutTheme = File.ReadAllText(Component("Layout", "MainLayout.razor.css"));
        var navigation = File.ReadAllText(Component("Layout", "NavMenu.razor"));

        Assert.Contains("class=\"btn btn-sm header-settings\"", layout);
        Assert.Contains("class=\"pwa-title-user\"", layout);
        Assert.Contains("class=\"pwa-admin-icon\"", layout);
        Assert.Matches(@"\.top-row\s*\{\s*display:\s*none;", layoutTheme);
        Assert.Matches(@"\.pwa-title-user\s*\{[^}]*display:\s*flex;", layoutTheme);
        Assert.Matches(
            @"<NavLink href=""users"">users</NavLink>\s*<NavLink href=""settings"">settings</NavLink>",
            navigation);
        Assert.Matches(
            @"<NavLink href=""settings"">settings</NavLink>[\s\S]*?<form method=""post"" action=""/logout"">",
            navigation);
        Assert.Contains("<AntiforgeryToken />", navigation);
        Assert.Contains("class=\"metro-menu-action\">sign out</button>", navigation);
    }

    private static string Component(params string[] parts) =>
        Path.Combine(RepositoryRoot(), "src", "PodcastTranscription.Web", "Components",
            Path.Combine(parts));

    private static string WebRoot(params string[] parts) =>
        Path.Combine(RepositoryRoot(), "src", "PodcastTranscription.Web", "wwwroot",
            Path.Combine(parts));

    private static string Source(params string[] parts) =>
        Path.Combine(RepositoryRoot(), "src", "PodcastTranscription.Web", Path.Combine(parts));

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
