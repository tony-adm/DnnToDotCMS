using DnnToDotCms.Crawler;

namespace DnnToDotCms.Tests;

public class CrawlLayoutExtractorTests
{
    private static readonly Uri BaseUrl = new("https://example.com/");

    // ------------------------------------------------------------------
    // ExtractLayout — basic scenarios
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractLayout_WithMainElement_PreservesLayoutAround()
    {
        string html = """
            <html>
            <head><title>Test</title></head>
            <body>
              <header><nav>Menu</nav></header>
              <main><p>Content</p></main>
              <footer>Footer</footer>
            </body>
            </html>
            """;

        var layout = CrawlLayoutExtractor.ExtractLayout(html, "test-theme", BaseUrl);

        Assert.NotNull(layout);
        Assert.Contains("<header>", layout.TemplateBody);
        Assert.Contains("<nav>", layout.TemplateBody);
        Assert.Contains("<footer>", layout.TemplateBody);
        Assert.Contains(CrawlLayoutExtractor.ContentPanePlaceholder, layout.TemplateBody);
        Assert.DoesNotContain("<p>Content</p>", layout.TemplateBody);
    }

    [Fact]
    public void ExtractLayout_WithRoleMain_UsesRoleMainElement()
    {
        string html = """
            <html>
            <head><title>Test</title></head>
            <body>
              <header>Header</header>
              <div role="main"><p>Main content</p></div>
              <footer>Footer</footer>
            </body>
            </html>
            """;

        var layout = CrawlLayoutExtractor.ExtractLayout(html, "test-theme", BaseUrl);

        Assert.NotNull(layout);
        Assert.Contains(CrawlLayoutExtractor.ContentPanePlaceholder, layout.TemplateBody);
        Assert.DoesNotContain("<p>Main content</p>", layout.TemplateBody);
        Assert.Contains("<header>", layout.TemplateBody);
    }

    [Fact]
    public void ExtractLayout_NoMainElement_FallsBackToPlaceholderOnly()
    {
        string html = """
            <html>
            <head><title>Test</title></head>
            <body>
              <div>Some content</div>
            </body>
            </html>
            """;

        var layout = CrawlLayoutExtractor.ExtractLayout(html, "test-theme", BaseUrl);

        Assert.NotNull(layout);
        Assert.Equal(CrawlLayoutExtractor.ContentPanePlaceholder, layout.TemplateBody);
    }

    [Fact]
    public void ExtractLayout_SetsThemeName()
    {
        string html = "<html><head></head><body><main>X</main></body></html>";

        var layout = CrawlLayoutExtractor.ExtractLayout(html, "my-site", BaseUrl);

        Assert.NotNull(layout);
        Assert.Equal("my-site", layout.ThemeName);
    }

    [Fact]
    public void ExtractLayout_SetsPaneMap_WithContentPane()
    {
        string html = "<html><head></head><body><main>X</main></body></html>";

        var layout = CrawlLayoutExtractor.ExtractLayout(html, "test", BaseUrl);

        Assert.NotNull(layout);
        Assert.Single(layout.PaneMap);
        Assert.True(layout.PaneMap.ContainsKey("ContentPane"));
        Assert.Equal(1, layout.PaneMap["ContentPane"]);
    }

    [Fact]
    public void ExtractLayout_NullHtml_ReturnsNull()
    {
        Assert.Null(CrawlLayoutExtractor.ExtractLayout(null!, "test", BaseUrl));
    }

    [Fact]
    public void ExtractLayout_EmptyHtml_ReturnsNull()
    {
        Assert.Null(CrawlLayoutExtractor.ExtractLayout("", "test", BaseUrl));
    }

    [Fact]
    public void ExtractLayout_NoBody_ReturnsNull()
    {
        string html = "<html><head><title>No body</title></head></html>";

        Assert.Null(CrawlLayoutExtractor.ExtractLayout(html, "test", BaseUrl));
    }

    // ------------------------------------------------------------------
    // ExtractLayout — CSS extraction
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractLayout_ExtractsCssFromHead()
    {
        string html = """
            <html>
            <head>
              <link rel="stylesheet" href="/css/style.css">
              <link rel="stylesheet" href="/css/theme.css">
            </head>
            <body><main>Content</main></body>
            </html>
            """;

        var layout = CrawlLayoutExtractor.ExtractLayout(html, "test", BaseUrl);

        Assert.NotNull(layout);
        Assert.Contains("/application/css/style.css", layout.TemplateHeader);
        Assert.Contains("/application/css/theme.css", layout.TemplateHeader);
    }

    [Fact]
    public void ExtractLayout_RewritesAbsoluteCssUrls()
    {
        string html = """
            <html>
            <head>
              <link rel="stylesheet" href="https://example.com/css/style.css">
            </head>
            <body><main>Content</main></body>
            </html>
            """;

        var layout = CrawlLayoutExtractor.ExtractLayout(html, "test", BaseUrl);

        Assert.NotNull(layout);
        Assert.Contains("/application/css/style.css", layout.TemplateHeader);
        Assert.DoesNotContain("https://example.com", layout.TemplateHeader);
    }

    [Fact]
    public void ExtractLayout_PreservesExternalCssUrls()
    {
        string html = """
            <html>
            <head>
              <link rel="stylesheet" href="https://cdn.other.com/lib.css">
            </head>
            <body><main>Content</main></body>
            </html>
            """;

        var layout = CrawlLayoutExtractor.ExtractLayout(html, "test", BaseUrl);

        Assert.NotNull(layout);
        Assert.Contains("https://cdn.other.com/lib.css", layout.TemplateHeader);
    }

    // ------------------------------------------------------------------
    // ExtractLayout — JS extraction
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractLayout_ExtractsScriptsToEndOfBody()
    {
        string html = """
            <html>
            <head><title>Test</title></head>
            <body>
              <header>Header</header>
              <script src="/js/app.js"></script>
              <main>Content</main>
              <footer>Footer</footer>
            </body>
            </html>
            """;

        var layout = CrawlLayoutExtractor.ExtractLayout(html, "test", BaseUrl);

        Assert.NotNull(layout);
        Assert.Contains("/application/js/app.js", layout.TemplateBody);
        // Script should be at the end of the template body
        int scriptPos = layout.TemplateBody.LastIndexOf("<script");
        int footerPos = layout.TemplateBody.IndexOf("<footer>");
        Assert.True(scriptPos > footerPos, "Script tag should appear after footer in template body");
    }

    [Fact]
    public void ExtractLayout_RewritesScriptUrls()
    {
        string html = """
            <html>
            <head></head>
            <body>
              <main>Content</main>
              <script src="https://example.com/js/main.js"></script>
            </body>
            </html>
            """;

        var layout = CrawlLayoutExtractor.ExtractLayout(html, "test", BaseUrl);

        Assert.NotNull(layout);
        Assert.Contains("/application/js/main.js", layout.TemplateBody);
    }

    // ------------------------------------------------------------------
    // ExtractLayout — asset path rewriting in template body
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractLayout_RewritesImageSrcInTemplate()
    {
        string html = """
            <html>
            <head></head>
            <body>
              <header><img src="/images/logo.png" alt="Logo"></header>
              <main>Content</main>
            </body>
            </html>
            """;

        var layout = CrawlLayoutExtractor.ExtractLayout(html, "test", BaseUrl);

        Assert.NotNull(layout);
        Assert.Contains("/application/images/logo.png", layout.TemplateBody);
    }

    [Fact]
    public void ExtractLayout_DoesNotDoubleRewriteApplicationPaths()
    {
        string html = """
            <html>
            <head></head>
            <body>
              <header><img src="/application/already.png" alt="Already rewritten"></header>
              <main>Content</main>
            </body>
            </html>
            """;

        var layout = CrawlLayoutExtractor.ExtractLayout(html, "test", BaseUrl);

        Assert.NotNull(layout);
        Assert.Contains("/application/already.png", layout.TemplateBody);
        Assert.DoesNotContain("/application/application/", layout.TemplateBody);
    }

    // ------------------------------------------------------------------
    // ExtractHeadReferences
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractHeadReferences_NoHead_ReturnsEmpty()
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml("<body>No head</body>");

        Assert.Equal(string.Empty, CrawlLayoutExtractor.ExtractHeadReferences(doc, BaseUrl));
    }

    [Fact]
    public void ExtractHeadReferences_MultipleStylesheets_ReturnsAll()
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml("""
            <html>
            <head>
              <link rel="stylesheet" href="/a.css">
              <link rel="stylesheet" href="/b.css">
            </head>
            <body></body>
            </html>
            """);

        string result = CrawlLayoutExtractor.ExtractHeadReferences(doc, BaseUrl);

        Assert.Contains("/application/a.css", result);
        Assert.Contains("/application/b.css", result);
    }

    [Fact]
    public void ExtractHeadReferences_IgnoresNonStylesheetLinks()
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml("""
            <html>
            <head>
              <link rel="icon" href="/favicon.ico">
              <link rel="stylesheet" href="/style.css">
            </head>
            <body></body>
            </html>
            """);

        string result = CrawlLayoutExtractor.ExtractHeadReferences(doc, BaseUrl);

        Assert.Contains("/application/style.css", result);
        Assert.DoesNotContain("favicon.ico", result);
    }

    // ------------------------------------------------------------------
    // ExtractScriptReferences
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractScriptReferences_CollectsExternalScripts()
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml("""
            <html>
            <head></head>
            <body>
              <script src="/js/a.js"></script>
              <script>console.log('inline');</script>
              <script src="/js/b.js"></script>
            </body>
            </html>
            """);

        string result = CrawlLayoutExtractor.ExtractScriptReferences(doc, BaseUrl);

        Assert.Contains("/application/js/a.js", result);
        Assert.Contains("/application/js/b.js", result);
        Assert.DoesNotContain("console.log", result);
    }

    // ------------------------------------------------------------------
    // RemoveScriptElements
    // ------------------------------------------------------------------

    [Fact]
    public void RemoveScriptElements_RemovesAllScripts()
    {
        string html = """
            <header>Header</header>
            <script src="/app.js"></script>
            <main>Content</main>
            <script>inline code</script>
            """;

        string result = CrawlLayoutExtractor.RemoveScriptElements(html);

        Assert.DoesNotContain("<script", result);
        Assert.Contains("<header>", result);
        Assert.Contains("<main>", result);
    }

    [Fact]
    public void RemoveScriptElements_EmptyHtml_ReturnsEmpty()
    {
        Assert.Equal("", CrawlLayoutExtractor.RemoveScriptElements(""));
    }

    [Fact]
    public void RemoveScriptElements_NoScripts_ReturnsOriginal()
    {
        string html = "<div>No scripts here</div>";
        string result = CrawlLayoutExtractor.RemoveScriptElements(html);
        Assert.Contains("No scripts here", result);
    }

    // ------------------------------------------------------------------
    // RewriteAssetRefsInTemplate
    // ------------------------------------------------------------------

    [Fact]
    public void RewriteAssetRefsInTemplate_RewritesAbsoluteUrls()
    {
        string html = """<img src="https://example.com/images/logo.png">""";

        string result = CrawlLayoutExtractor.RewriteAssetRefsInTemplate(html, BaseUrl);

        Assert.Contains("/application/images/logo.png", result);
        Assert.DoesNotContain("https://example.com", result);
    }

    [Fact]
    public void RewriteAssetRefsInTemplate_RewritesRootRelativeHref()
    {
        string html = """<link href="/css/style.css">""";

        string result = CrawlLayoutExtractor.RewriteAssetRefsInTemplate(html, BaseUrl);

        Assert.Contains("/application/css/style.css", result);
    }

    [Fact]
    public void RewriteAssetRefsInTemplate_SkipsAlreadyApplicationPrefixed()
    {
        string html = """<img src="/application/images/logo.png">""";

        string result = CrawlLayoutExtractor.RewriteAssetRefsInTemplate(html, BaseUrl);

        Assert.Contains("/application/images/logo.png", result);
        Assert.DoesNotContain("/application/application/", result);
    }

    [Fact]
    public void RewriteAssetRefsInTemplate_PreservesExternalUrls()
    {
        string html = """<img src="https://cdn.other.com/img.png">""";

        string result = CrawlLayoutExtractor.RewriteAssetRefsInTemplate(html, BaseUrl);

        Assert.Contains("https://cdn.other.com/img.png", result);
    }

    // ------------------------------------------------------------------
    // Integration: full page HTML → layout → template body
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractLayout_ComplexPage_ProducesUsableTemplate()
    {
        string html = """
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>My Site</title>
              <link rel="stylesheet" href="/css/bootstrap.css">
              <link rel="stylesheet" href="/css/custom.css">
            </head>
            <body>
              <header class="site-header">
                <nav class="navbar">
                  <a href="/" class="brand">My Site</a>
                  <ul>
                    <li><a href="/about">About</a></li>
                    <li><a href="/contact">Contact</a></li>
                  </ul>
                </nav>
              </header>
              <main class="content-area">
                <h1>Welcome</h1>
                <p>This is the main content.</p>
              </main>
              <footer class="site-footer">
                <p>&copy; 2024 My Site</p>
              </footer>
              <script src="/js/app.js"></script>
            </body>
            </html>
            """;

        var layout = CrawlLayoutExtractor.ExtractLayout(html, "my-site", BaseUrl);

        Assert.NotNull(layout);

        // Template body should contain layout structure
        Assert.Contains("site-header", layout.TemplateBody);
        Assert.Contains("navbar", layout.TemplateBody);
        Assert.Contains("site-footer", layout.TemplateBody);
        Assert.Contains(CrawlLayoutExtractor.ContentPanePlaceholder, layout.TemplateBody);

        // Content should be replaced
        Assert.DoesNotContain("<h1>Welcome</h1>", layout.TemplateBody);
        Assert.DoesNotContain("This is the main content", layout.TemplateBody);

        // CSS should be in header
        Assert.Contains("/application/css/bootstrap.css", layout.TemplateHeader);
        Assert.Contains("/application/css/custom.css", layout.TemplateHeader);

        // JS should be in body
        Assert.Contains("/application/js/app.js", layout.TemplateBody);

        // Theme name
        Assert.Equal("my-site", layout.ThemeName);

        // Pane map
        Assert.Contains("ContentPane", layout.PaneMap.Keys);
    }
}
