using DnnToDotCms.Crawler;
using HtmlAgilityPack;

namespace DnnToDotCms.Tests;

public class WebCrawlerTests
{
    // -----------------------------------------------------------------------
    // ExtractTitle
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractTitle_ReturnsTitle()
    {
        var doc = LoadHtml("<html><head><title>Hello World</title></head><body></body></html>");
        Assert.Equal("Hello World", WebCrawler.ExtractTitle(doc));
    }

    [Fact]
    public void ExtractTitle_NoTitleElement_ReturnsEmpty()
    {
        var doc = LoadHtml("<html><head></head><body></body></html>");
        Assert.Equal("", WebCrawler.ExtractTitle(doc));
    }

    [Fact]
    public void ExtractTitle_DecodesHtmlEntities()
    {
        var doc = LoadHtml("<html><head><title>Tom &amp; Jerry</title></head><body></body></html>");
        Assert.Equal("Tom & Jerry", WebCrawler.ExtractTitle(doc));
    }

    // -----------------------------------------------------------------------
    // ExtractDescription
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractDescription_ReturnsContent()
    {
        var doc = LoadHtml(
            "<html><head><meta name=\"description\" content=\"A test page\"></head><body></body></html>");
        Assert.Equal("A test page", WebCrawler.ExtractDescription(doc));
    }

    [Fact]
    public void ExtractDescription_NoMeta_ReturnsEmpty()
    {
        var doc = LoadHtml("<html><head></head><body></body></html>");
        Assert.Equal("", WebCrawler.ExtractDescription(doc));
    }

    [Fact]
    public void ExtractDescription_CapitalName_StillMatches()
    {
        var doc = LoadHtml(
            "<html><head><meta name=\"Description\" content=\"Upper case\"></head><body></body></html>");
        Assert.Equal("Upper case", WebCrawler.ExtractDescription(doc));
    }

    // -----------------------------------------------------------------------
    // ExtractBodyContent
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractBodyContent_PrefersMainElement()
    {
        var doc = LoadHtml(
            "<html><body><header>Nav</header><main><p>Main content</p></main><footer>Foot</footer></body></html>");
        Assert.Equal("<p>Main content</p>", WebCrawler.ExtractBodyContent(doc));
    }

    [Fact]
    public void ExtractBodyContent_PrefersRoleMain()
    {
        var doc = LoadHtml(
            "<html><body><div role=\"main\"><p>Content</p></div></body></html>");
        Assert.Equal("<p>Content</p>", WebCrawler.ExtractBodyContent(doc));
    }

    [Fact]
    public void ExtractBodyContent_FallsBackToBody()
    {
        var doc = LoadHtml("<html><body><p>Body text</p></body></html>");
        Assert.Equal("<p>Body text</p>", WebCrawler.ExtractBodyContent(doc));
    }

    [Fact]
    public void ExtractBodyContent_NoBody_ReturnsEmpty()
    {
        var doc = LoadHtml("<html></html>");
        Assert.Equal("", WebCrawler.ExtractBodyContent(doc));
    }

    // -----------------------------------------------------------------------
    // ExtractLinks
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractLinks_ResolvesRelativeAndAbsoluteUrls()
    {
        var doc = LoadHtml("""
            <html><body>
                <a href="/about">About</a>
                <a href="https://example.com/contact">Contact</a>
                <a href="page.html">Page</a>
            </body></html>
            """);
        var baseUri = new Uri("https://example.com/");
        var links = WebCrawler.ExtractLinks(doc, baseUri).ToList();

        Assert.Contains(links, l => l.AbsoluteUri == "https://example.com/about");
        Assert.Contains(links, l => l.AbsoluteUri == "https://example.com/contact");
        Assert.Contains(links, l => l.AbsoluteUri == "https://example.com/page.html");
    }

    [Fact]
    public void ExtractLinks_IgnoresFragmentsMailtoTelJavascript()
    {
        var doc = LoadHtml("""
            <html><body>
                <a href="#section">Anchor</a>
                <a href="mailto:test@example.com">Email</a>
                <a href="tel:+1234567890">Phone</a>
                <a href="javascript:void(0)">JS</a>
            </body></html>
            """);
        var baseUri = new Uri("https://example.com/");
        var links = WebCrawler.ExtractLinks(doc, baseUri).ToList();

        Assert.Empty(links);
    }

    [Fact]
    public void ExtractLinks_NoAnchors_ReturnsEmpty()
    {
        var doc = LoadHtml("<html><body><p>No links</p></body></html>");
        var baseUri = new Uri("https://example.com/");
        Assert.Empty(WebCrawler.ExtractLinks(doc, baseUri));
    }

    // -----------------------------------------------------------------------
    // ExtractAssetUrls
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractAssetUrls_FindsImagesStylesScriptsAndIcons()
    {
        var doc = LoadHtml("""
            <html>
            <head>
                <link rel="stylesheet" href="/css/style.css">
                <link rel="icon" href="/favicon.ico">
            </head>
            <body>
                <img src="/images/logo.png">
                <script src="/js/app.js"></script>
            </body>
            </html>
            """);
        var baseUri = new Uri("https://example.com/");
        var urls = WebCrawler.ExtractAssetUrls(doc, baseUri).ToList();

        Assert.Equal(4, urls.Count);
        Assert.Contains(urls, u => u.AbsolutePath == "/images/logo.png");
        Assert.Contains(urls, u => u.AbsolutePath == "/css/style.css");
        Assert.Contains(urls, u => u.AbsolutePath == "/js/app.js");
        Assert.Contains(urls, u => u.AbsolutePath == "/favicon.ico");
    }

    // -----------------------------------------------------------------------
    // NormalizeUrl
    // -----------------------------------------------------------------------

    [Fact]
    public void NormalizeUrl_StripsFragmentAndTrailingSlash()
    {
        var url = new Uri("https://EXAMPLE.COM/About/#team");
        string normalized = WebCrawler.NormalizeUrl(url);
        Assert.Equal("https://example.com:443/about", normalized);
    }

    [Fact]
    public void NormalizeUrl_PreservesQueryString()
    {
        var url = new Uri("https://example.com/search?q=test");
        string normalized = WebCrawler.NormalizeUrl(url);
        Assert.Contains("q=test", normalized);
    }

    // -----------------------------------------------------------------------
    // DeriveRelativePath
    // -----------------------------------------------------------------------

    [Fact]
    public void DeriveRelativePath_StripsLeadingSlashAndQuery()
    {
        var assetUrl = new Uri("https://example.com/images/logo.png?v=2");
        var baseUri = new Uri("https://example.com/");
        Assert.Equal("images/logo.png", WebCrawler.DeriveRelativePath(assetUrl, baseUri));
    }

    [Fact]
    public void DeriveRelativePath_EmptyPath_ReturnsIndex()
    {
        var assetUrl = new Uri("https://example.com/");
        var baseUri = new Uri("https://example.com/");
        Assert.Equal("index", WebCrawler.DeriveRelativePath(assetUrl, baseUri));
    }

    // -----------------------------------------------------------------------
    // CrawlAsync — integration-style test with mock HTTP handler
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CrawlAsync_CrawlsSinglePage()
    {
        string html = """
            <html>
            <head><title>Home</title><meta name="description" content="Welcome"></head>
            <body><main><p>Hello</p></main></body>
            </html>
            """;

        var handler = new MockHttpHandler(new Dictionary<string, (string Content, string ContentType)>
        {
            ["/"] = (html, "text/html"),
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };
        var crawler = new WebCrawler(http, maxPages: 10);
        var result = await crawler.CrawlAsync(new Uri("https://test.local/"));

        Assert.Single(result.Pages);
        Assert.Equal("Home", result.Pages[0].Title);
        Assert.Equal("Welcome", result.Pages[0].Description);
        Assert.Contains("<p>Hello</p>", result.Pages[0].HtmlBody);
    }

    [Fact]
    public async Task CrawlAsync_FollowsInternalLinks()
    {
        string homePage = """
            <html>
            <head><title>Home</title></head>
            <body><main><a href="/about">About</a></main></body>
            </html>
            """;
        string aboutPage = """
            <html>
            <head><title>About</title></head>
            <body><main><p>About us</p></main></body>
            </html>
            """;

        var handler = new MockHttpHandler(new Dictionary<string, (string Content, string ContentType)>
        {
            ["/"] = (homePage, "text/html"),
            ["/about"] = (aboutPage, "text/html"),
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };
        var crawler = new WebCrawler(http, maxPages: 10);
        var result = await crawler.CrawlAsync(new Uri("https://test.local/"));

        Assert.Equal(2, result.Pages.Count);
        Assert.Contains(result.Pages, p => p.Title == "Home");
        Assert.Contains(result.Pages, p => p.Title == "About");
    }

    [Fact]
    public async Task CrawlAsync_RespectsMaxPages()
    {
        string page = """
            <html>
            <head><title>Page</title></head>
            <body><main>
                <a href="/p1">P1</a><a href="/p2">P2</a><a href="/p3">P3</a>
            </main></body>
            </html>
            """;

        var pages = new Dictionary<string, (string Content, string ContentType)>
        {
            ["/"] = (page, "text/html"),
            ["/p1"] = (page, "text/html"),
            ["/p2"] = (page, "text/html"),
            ["/p3"] = (page, "text/html"),
        };

        var handler = new MockHttpHandler(pages);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };
        var crawler = new WebCrawler(http, maxPages: 2);
        var result = await crawler.CrawlAsync(new Uri("https://test.local/"));

        Assert.Equal(2, result.Pages.Count);
    }

    [Fact]
    public async Task CrawlAsync_DownloadsAssets()
    {
        string html = """
            <html>
            <head><link rel="stylesheet" href="/css/style.css"></head>
            <body><main><img src="/img/logo.png"></main></body>
            </html>
            """;

        var handler = new MockHttpHandler(
            new Dictionary<string, (string Content, string ContentType)>
            {
                ["/"] = (html, "text/html"),
            },
            new Dictionary<string, (byte[] Content, string ContentType)>
            {
                ["/css/style.css"] = ("body{}"u8.ToArray(), "text/css"),
                ["/img/logo.png"] = (new byte[] { 0x89, 0x50 }, "image/png"),
            });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };
        var crawler = new WebCrawler(http, maxPages: 10);
        var result = await crawler.CrawlAsync(new Uri("https://test.local/"));

        Assert.Equal(2, result.Assets.Count);
        Assert.Contains(result.Assets, a => a.RelativePath == "css/style.css");
        Assert.Contains(result.Assets, a => a.RelativePath == "img/logo.png");
    }

    [Fact]
    public async Task CrawlAsync_SkipsExternalLinks()
    {
        string html = """
            <html>
            <head><title>Home</title></head>
            <body><main>
                <a href="https://external.com/page">External</a>
                <a href="/local">Local</a>
            </main></body>
            </html>
            """;
        string localPage = """
            <html><head><title>Local</title></head>
            <body><main><p>Local page</p></main></body></html>
            """;

        var handler = new MockHttpHandler(new Dictionary<string, (string Content, string ContentType)>
        {
            ["/"] = (html, "text/html"),
            ["/local"] = (localPage, "text/html"),
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };
        var crawler = new WebCrawler(http, maxPages: 10);
        var result = await crawler.CrawlAsync(new Uri("https://test.local/"));

        // Only the two local pages should be crawled; the external link is ignored.
        Assert.Equal(2, result.Pages.Count);
        Assert.DoesNotContain(result.Pages, p => p.Url.Host == "external.com");
    }

    [Fact]
    public void Constructor_ThrowsOnNullHttpClient()
    {
        Assert.Throws<ArgumentNullException>(() => new WebCrawler(null!, 10));
    }

    [Fact]
    public void Constructor_ThrowsOnZeroMaxPages()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebCrawler(http, 0));
    }

    [Fact]
    public async Task CrawlAsync_ThrowsOnRelativeUrl()
    {
        using var http = new HttpClient();
        var crawler = new WebCrawler(http, 10);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            crawler.CrawlAsync(new Uri("/relative", UriKind.Relative)));
    }

    // -----------------------------------------------------------------------
    // Mock HTTP handler for test isolation
    // -----------------------------------------------------------------------

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Content, string ContentType)> _htmlPages;
        private readonly Dictionary<string, (byte[] Content, string ContentType)> _binaryAssets;

        public MockHttpHandler(
            Dictionary<string, (string Content, string ContentType)> htmlPages,
            Dictionary<string, (byte[] Content, string ContentType)>? binaryAssets = null)
        {
            _htmlPages = htmlPages;
            _binaryAssets = binaryAssets ?? new();
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;

            if (_htmlPages.TryGetValue(path, out var htmlEntry))
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(htmlEntry.Content, System.Text.Encoding.UTF8,
                        htmlEntry.ContentType),
                };
                return Task.FromResult(response);
            }

            if (_binaryAssets.TryGetValue(path, out var binaryEntry))
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(binaryEntry.Content),
                };
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(binaryEntry.ContentType);
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    // -----------------------------------------------------------------------
    // Helper
    // -----------------------------------------------------------------------

    private static HtmlDocument LoadHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }
}
