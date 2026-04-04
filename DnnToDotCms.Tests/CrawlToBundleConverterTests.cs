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
        Assert.Equal(2, ct.Fields.Count);
        Assert.Contains(ct.Fields, f => f.Variable == "title");
        Assert.Contains(ct.Fields, f => f.Variable == "body");
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

    // -----------------------------------------------------------------------
    // ConvertPages
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
        Assert.Equal("images/", files[0].FolderPath);
        Assert.Equal("image/png", files[0].MimeType);
        Assert.Equal(content, files[0].Content);
    }

    [Fact]
    public void ConvertAssets_RootLevelFile_EmptyFolderPath()
    {
        var asset = new CrawledAsset(
            new Uri("https://example.com/favicon.ico"),
            "favicon.ico",
            "image/x-icon",
            [0]);

        var result = new CrawlResult(BaseUrl, [], [asset]);
        var files = CrawlToBundleConverter.ConvertAssets(result);

        Assert.Equal("favicon.ico", files[0].FileName);
        Assert.Equal("", files[0].FolderPath);
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
}
