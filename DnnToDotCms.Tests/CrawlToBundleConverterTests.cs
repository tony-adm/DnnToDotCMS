using DnnToDotCms.Crawler;

namespace DnnToDotCms.Tests;

public class CrawlToBundleConverterTests
{
    private static readonly Uri BaseUrl = new("https://example.com/");

    // -----------------------------------------------------------------------
    // BuildHtmlContentType
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildHtmlContentType_ReturnsValidContentType()
    {
        var ct = CrawlToBundleConverter.BuildHtmlContentType();

        Assert.Equal("HTMLContent", ct.Name);
        Assert.Equal("htmlContent", ct.Variable);
        Assert.Equal(3, ct.Fields.Count);
        Assert.Contains(ct.Fields, f => f.Variable == "title");
        Assert.Contains(ct.Fields, f => f.Variable == "body");
        Assert.Contains(ct.Fields, f => f.Variable == "image");
    }

    [Fact]
    public void BuildHtmlContentType_TitleFieldIsRequired()
    {
        var ct = CrawlToBundleConverter.BuildHtmlContentType();
        var titleField = ct.Fields.Single(f => f.Variable == "title");
        Assert.True(titleField.Required);
    }

    [Fact]
    public void BuildHtmlContentType_BodyFieldIsWysiwyg()
    {
        var ct = CrawlToBundleConverter.BuildHtmlContentType();
        var bodyField = ct.Fields.Single(f => f.Variable == "body");
        Assert.Equal("LONG_TEXT", bodyField.DataType);
        Assert.Equal("WYSIWYG", bodyField.FieldTypeLabel);
    }

    [Fact]
    public void BuildHtmlContentType_MatchesExportPathContentType()
    {
        // The crawl content type must match the export-path htmlContent
        // definition: same description, icon, and field structure.
        var ct = CrawlToBundleConverter.BuildHtmlContentType();

        Assert.Equal("Converted from DNN HTML module", ct.Description);
        Assert.Equal("fa fa-code", ct.Icon);

        // Image field should be a searchable text field with a hint.
        var imageField = ct.Fields.Single(f => f.Variable == "image");
        Assert.Equal("TEXT", imageField.DataType);
        Assert.Equal("Text", imageField.FieldTypeLabel);
        Assert.Equal("Icon or image URL for container rendering", imageField.Hint);
    }

    // -----------------------------------------------------------------------
    // Convert (paired content + pages)
    // -----------------------------------------------------------------------

    [Fact]
    public void Convert_LinksContentToPageViaTabUniqueId()
    {
        var result = MakeCrawlResult(
            new CrawledPage(new Uri("https://example.com/about"), "About Us", "Our story", "<p>Hello</p>"));

        var (contents, pages) = CrawlToBundleConverter.Convert(result);

        Assert.Single(contents);
        Assert.Single(pages);
        Assert.Equal(pages[0].UniqueId, contents[0].TabUniqueId);
    }

    [Fact]
    public void Convert_SetsContentPaneName()
    {
        var result = MakeCrawlResult(
            new CrawledPage(new Uri("https://example.com/about"), "About Us", "", "<p>Hi</p>"));

        var (contents, _) = CrawlToBundleConverter.Convert(result);

        Assert.Equal("ContentPane", contents[0].PaneName);
    }

    [Fact]
    public void Convert_MultiplePages_EachLinked()
    {
        var result = MakeCrawlResult(
            new CrawledPage(new Uri("https://example.com/"), "Home", "", "<p>Home</p>"),
            new CrawledPage(new Uri("https://example.com/about"), "About", "", "<p>About</p>"));

        var (contents, pages) = CrawlToBundleConverter.Convert(result);

        Assert.Equal(2, contents.Count);
        Assert.Equal(2, pages.Count);
        Assert.Equal(pages[0].UniqueId, contents[0].TabUniqueId);
        Assert.Equal(pages[1].UniqueId, contents[1].TabUniqueId);
        Assert.NotEqual(pages[0].UniqueId, pages[1].UniqueId);
    }

    // -----------------------------------------------------------------------
    // ConvertPages (with portal pages linkage)
    // -----------------------------------------------------------------------

    [Fact]
    public void ConvertPages_MapsFieldsCorrectly()
    {
        var result = MakeCrawlResult(
            new CrawledPage(new Uri("https://example.com/about"), "About Us", "Our story", "<p>Hello</p>"));

        var contents = CrawlToBundleConverter.ConvertPages(result);

        Assert.Single(contents);
        Assert.Equal("About Us", contents[0].Title);
        Assert.Equal("<p>Hello</p>", contents[0].HtmlBody);
    }

    [Fact]
    public void ConvertPages_EmptyTitle_UsesSlug()
    {
        var result = MakeCrawlResult(
            new CrawledPage(new Uri("https://example.com/contact-us"), "", "", "<p>Contact</p>"));

        var contents = CrawlToBundleConverter.ConvertPages(result);

        Assert.Equal("contact-us", contents[0].Title);
    }

    [Fact]
    public void ConvertPages_MultiplePages()
    {
        var result = MakeCrawlResult(
            new CrawledPage(new Uri("https://example.com/"), "Home", "", "<p>Home</p>"),
            new CrawledPage(new Uri("https://example.com/about"), "About", "", "<p>About</p>"),
            new CrawledPage(new Uri("https://example.com/contact"), "Contact", "", "<p>Contact</p>"));

        var contents = CrawlToBundleConverter.ConvertPages(result);

        Assert.Equal(3, contents.Count);
    }

    [Fact]
    public void ConvertPages_WithPortalPages_LinksViaTabUniqueId()
    {
        var result = MakeCrawlResult(
            new CrawledPage(new Uri("https://example.com/about"), "About", "", "<p>About</p>"));

        var pages = CrawlToBundleConverter.ConvertPortalPages(result);
        var contents = CrawlToBundleConverter.ConvertPages(result, pages);

        Assert.Equal(pages[0].UniqueId, contents[0].TabUniqueId);
    }

    [Fact]
    public void ConvertPages_WithPortalPages_SetsContentPane()
    {
        var result = MakeCrawlResult(
            new CrawledPage(new Uri("https://example.com/about"), "About", "", "<p>About</p>"));

        var pages = CrawlToBundleConverter.ConvertPortalPages(result);
        var contents = CrawlToBundleConverter.ConvertPages(result, pages);

        Assert.Equal("ContentPane", contents[0].PaneName);
    }

    [Fact]
    public void ConvertPages_WithoutPortalPages_EmptyTabUniqueId()
    {
        var result = MakeCrawlResult(
            new CrawledPage(new Uri("https://example.com/about"), "About", "", "<p>About</p>"));

        var contents = CrawlToBundleConverter.ConvertPages(result);

        Assert.Equal("", contents[0].TabUniqueId);
    }

    // -----------------------------------------------------------------------
    // ConvertPortalPages
    // -----------------------------------------------------------------------

    [Fact]
    public void ConvertPortalPages_CreatesPageWithSlugAndTitle()
    {
        var result = MakeCrawlResult(
            new CrawledPage(new Uri("https://example.com/about"), "About Us", "Desc", "<p>Hi</p>"));

        var pages = CrawlToBundleConverter.ConvertPortalPages(result);

        Assert.Single(pages);
        Assert.Equal("about", pages[0].Name);
        Assert.Equal("About Us", pages[0].Title);
        Assert.Equal("Desc", pages[0].Description);
        Assert.Equal("//about", pages[0].TabPath);
        Assert.Equal(0, pages[0].Level);
        Assert.True(pages[0].IsVisible);
    }

    [Fact]
    public void ConvertPortalPages_RootUrl_ProducesHomePage()
    {
        var result = MakeCrawlResult(
            new CrawledPage(new Uri("https://example.com/"), "Welcome", "", "<p>Home</p>"));

        var pages = CrawlToBundleConverter.ConvertPortalPages(result);

        Assert.Equal("home", pages[0].Name);
        Assert.Equal("//home", pages[0].TabPath);
    }

    [Fact]
    public void ConvertPortalPages_EmptyTitle_FallsBackToSlug()
    {
        var result = MakeCrawlResult(
            new CrawledPage(new Uri("https://example.com/pricing"), "", "", "<p>Pricing</p>"));

        var pages = CrawlToBundleConverter.ConvertPortalPages(result);

        Assert.Equal("pricing", pages[0].Title);
    }

    [Fact]
    public void ConvertPortalPages_AssignsUniqueIds()
    {
        var result = MakeCrawlResult(
            new CrawledPage(new Uri("https://example.com/a"), "A", "", "<p>A</p>"),
            new CrawledPage(new Uri("https://example.com/b"), "B", "", "<p>B</p>"));

        var pages = CrawlToBundleConverter.ConvertPortalPages(result);

        Assert.Equal(2, pages.Count);
        Assert.NotEqual(pages[0].UniqueId, pages[1].UniqueId);
    }

    // -----------------------------------------------------------------------
    // ConvertAssets
    // -----------------------------------------------------------------------

    [Fact]
    public void ConvertAssets_MapsFieldsCorrectly()
    {
        byte[] content = [1, 2, 3];
        var asset = new CrawledAsset(
            new Uri("https://example.com/images/logo.png"),
            "images/logo.png",
            "image/png",
            content);

        var result = new CrawlResult(BaseUrl, [], [asset]);
        var files = CrawlToBundleConverter.ConvertAssets(result);

        Assert.Single(files);
        Assert.Equal("logo.png", files[0].FileName);
        Assert.Equal("application/images/", files[0].FolderPath);
        Assert.Equal("image/png", files[0].MimeType);
        Assert.Equal(content, files[0].Content);
    }

    [Fact]
    public void ConvertAssets_RootLevelFile_ApplicationFolderPath()
    {
        var asset = new CrawledAsset(
            new Uri("https://example.com/favicon.ico"),
            "favicon.ico",
            "image/x-icon",
            [0]);

        var result = new CrawlResult(BaseUrl, [], [asset]);
        var files = CrawlToBundleConverter.ConvertAssets(result);

        Assert.Equal("favicon.ico", files[0].FileName);
        Assert.Equal("application/", files[0].FolderPath);
    }

    [Fact]
    public void ConvertAssets_AssignsUniqueIds()
    {
        var assets = new[]
        {
            new CrawledAsset(new Uri("https://example.com/a.png"), "a.png", "image/png", [1]),
            new CrawledAsset(new Uri("https://example.com/b.png"), "b.png", "image/png", [2]),
        };

        var result = new CrawlResult(BaseUrl, [], assets);
        var files = CrawlToBundleConverter.ConvertAssets(result);

        Assert.Equal(2, files.Count);
        Assert.NotEqual(files[0].UniqueId, files[1].UniqueId);
        Assert.NotEqual(files[0].VersionGuid, files[1].VersionGuid);
    }

    // -----------------------------------------------------------------------
    // RewriteAssetPaths
    // -----------------------------------------------------------------------

    [Fact]
    public void RewriteAssetPaths_RewritesRootRelativeReferences()
    {
        var asset = new CrawledAsset(
            new Uri("https://example.com/images/logo.png"),
            "images/logo.png", "image/png", [1]);
        var result = new CrawlResult(BaseUrl, [], [asset]);

        string html = """<img src="/images/logo.png" alt="Logo">""";
        string rewritten = CrawlToBundleConverter.RewriteAssetPaths(html, result);

        Assert.Equal("""<img src="/application/images/logo.png" alt="Logo">""", rewritten);
    }

    [Fact]
    public void RewriteAssetPaths_RewritesAbsoluteUrls()
    {
        var asset = new CrawledAsset(
            new Uri("https://example.com/css/style.css"),
            "css/style.css", "text/css", [1]);
        var result = new CrawlResult(BaseUrl, [], [asset]);

        string html = """<link href="https://example.com/css/style.css">""";
        string rewritten = CrawlToBundleConverter.RewriteAssetPaths(html, result);

        Assert.Contains("/application/css/style.css", rewritten);
        Assert.DoesNotContain("https://example.com/css/style.css", rewritten);
    }

    [Fact]
    public void RewriteAssetPaths_DoesNotRewritePlainText()
    {
        var asset = new CrawledAsset(
            new Uri("https://example.com/images/logo.png"),
            "images/logo.png", "image/png", [1]);
        var result = new CrawlResult(BaseUrl, [], [asset]);

        string html = "<p>See /images/logo.png for details</p>";
        string rewritten = CrawlToBundleConverter.RewriteAssetPaths(html, result);

        // Plain text references should NOT be rewritten.
        Assert.Equal(html, rewritten);
    }

    [Fact]
    public void RewriteAssetPaths_NoAssets_ReturnsOriginal()
    {
        var result = new CrawlResult(BaseUrl, [], []);
        string html = "<p>Hello</p>";
        Assert.Equal(html, CrawlToBundleConverter.RewriteAssetPaths(html, result));
    }

    [Fact]
    public void RewriteAssetPaths_SingleQuoteAttribute()
    {
        var asset = new CrawledAsset(
            new Uri("https://example.com/js/main.js"),
            "js/main.js", "application/javascript", [1]);
        var result = new CrawlResult(BaseUrl, [], [asset]);

        string html = "<script src='/js/main.js'></script>";
        string rewritten = CrawlToBundleConverter.RewriteAssetPaths(html, result);

        Assert.Equal("<script src='/application/js/main.js'></script>", rewritten);
    }

    [Fact]
    public void RewriteAssetPaths_CssUrlFunction()
    {
        var asset = new CrawledAsset(
            new Uri("https://example.com/images/bg.jpg"),
            "images/bg.jpg", "image/jpeg", [1]);
        var result = new CrawlResult(BaseUrl, [], [asset]);

        string html = """<div style="background: url(/images/bg.jpg)"></div>""";
        string rewritten = CrawlToBundleConverter.RewriteAssetPaths(html, result);

        Assert.Contains("url(/application/images/bg.jpg)", rewritten);
    }

    [Fact]
    public void Convert_RewritesHtmlBodyAssetPaths()
    {
        var page = new CrawledPage(
            new Uri("https://example.com/about"),
            "About", "", """<img src="/images/photo.jpg">""");
        var asset = new CrawledAsset(
            new Uri("https://example.com/images/photo.jpg"),
            "images/photo.jpg", "image/jpeg", [1]);

        var result = new CrawlResult(BaseUrl, [page], [asset]);
        var (contents, _) = CrawlToBundleConverter.Convert(result);

        Assert.Contains("/application/images/photo.jpg", contents[0].HtmlBody);
        Assert.DoesNotContain("\"/images/photo.jpg\"", contents[0].HtmlBody);
    }

    [Fact]
    public void ConvertPages_RewritesHtmlBodyAssetPaths()
    {
        // ConvertPages must also rewrite asset paths, not just Convert().
        var page = new CrawledPage(
            new Uri("https://example.com/about"),
            "About", "", """<img src="/images/photo.jpg">""");
        var asset = new CrawledAsset(
            new Uri("https://example.com/images/photo.jpg"),
            "images/photo.jpg", "image/jpeg", [1]);

        var result = new CrawlResult(BaseUrl, [page], [asset]);
        var contents = CrawlToBundleConverter.ConvertPages(result);

        Assert.Contains("/application/images/photo.jpg", contents[0].HtmlBody);
        Assert.DoesNotContain("\"/images/photo.jpg\"", contents[0].HtmlBody);
    }

    [Fact]
    public void RewriteAssetPaths_UnquotedAttribute()
    {
        var asset = new CrawledAsset(
            new Uri("https://example.com/images/logo.png"),
            "images/logo.png", "image/png", [1]);
        var result = new CrawlResult(BaseUrl, [], [asset]);

        string html = "<img src=/images/logo.png>";
        string rewritten = CrawlToBundleConverter.RewriteAssetPaths(html, result);

        Assert.Equal("<img src=/application/images/logo.png>", rewritten);
    }

    [Fact]
    public void RewriteAssetPaths_MultipleAssetsInSameHtml()
    {
        var assets = new[]
        {
            new CrawledAsset(new Uri("https://example.com/images/a.png"), "images/a.png", "image/png", [1]),
            new CrawledAsset(new Uri("https://example.com/css/style.css"), "css/style.css", "text/css", [2]),
        };
        var result = new CrawlResult(BaseUrl, [], assets);

        string html = """<link href="/css/style.css"><img src="/images/a.png">""";
        string rewritten = CrawlToBundleConverter.RewriteAssetPaths(html, result);

        Assert.Contains("/application/images/a.png", rewritten);
        Assert.Contains("/application/css/style.css", rewritten);
        Assert.DoesNotContain("\"/images/a.png\"", rewritten);
        Assert.DoesNotContain("\"/css/style.css\"", rewritten);
    }

    [Fact]
    public void RewriteAssetPaths_DeepNestedPath()
    {
        var asset = new CrawledAsset(
            new Uri("https://example.com/assets/fonts/roboto/roboto.woff2"),
            "assets/fonts/roboto/roboto.woff2", "font/woff2", [1]);
        var result = new CrawlResult(BaseUrl, [], [asset]);

        string html = """url("/assets/fonts/roboto/roboto.woff2")""";
        string rewritten = CrawlToBundleConverter.RewriteAssetPaths(html, result);

        Assert.Contains("/application/assets/fonts/roboto/roboto.woff2", rewritten);
    }

    // -----------------------------------------------------------------------
    // DeriveSlug
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("https://example.com/", "home")]
    [InlineData("https://example.com/about", "about")]
    [InlineData("https://example.com/about/team", "about-team")]
    [InlineData("https://example.com/Contact%20Us", "contact%20us")]
    public void DeriveSlug_ProducesExpectedResult(string url, string expected)
    {
        Assert.Equal(expected, CrawlToBundleConverter.DeriveSlug(new Uri(url)));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static CrawlResult MakeCrawlResult(params CrawledPage[] pages)
        => new(BaseUrl, pages, []);

    private static CrawlResult MakeCrawlResult(CrawledPage[] pages, CrawledAsset[] assets)
        => new(BaseUrl, pages, assets);
}
