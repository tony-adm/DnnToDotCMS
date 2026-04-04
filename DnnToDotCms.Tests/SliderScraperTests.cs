using DnnToDotCms.Crawler;
using DnnToDotCms.Parser;

namespace DnnToDotCms.Tests;

public class SliderScraperTests
{
    private static readonly Uri BaseUrl = new("https://example.com/");

    // ------------------------------------------------------------------
    // ExtractSlides — FisSlider patterns
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractSlides_FisSliderContainer_ExtractsSlides()
    {
        string html = """
            <html><body>
            <div class="fisSlider">
              <div class="slide">
                <a href="/personal"><img src="/images/slide1.jpg" alt="Personal Banking"></a>
                <h3>Personal Banking</h3>
                <p>Great rates on checking accounts</p>
              </div>
              <div class="slide">
                <a href="/business"><img src="/images/slide2.jpg" alt="Business Banking"></a>
                <h3>Business Banking</h3>
                <p>Solutions for your business</p>
              </div>
            </div>
            </body></html>
            """;

        var slides = SliderScraper.ExtractSlides(html, BaseUrl);

        Assert.Equal(2, slides.Count);
        Assert.Equal("/images/slide1.jpg", slides[0].ImageUrl);
        Assert.Equal("/personal", slides[0].LinkUrl);
        Assert.Equal("Personal Banking", slides[0].Caption);
        Assert.Equal("Great rates on checking accounts", slides[0].Description);
        Assert.Equal("/images/slide2.jpg", slides[1].ImageUrl);
        Assert.Equal("/business", slides[1].LinkUrl);
    }

    // ------------------------------------------------------------------
    // ExtractSlides — Bootstrap carousel patterns
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractSlides_BootstrapCarousel_ExtractsSlides()
    {
        string html = """
            <html><body>
            <div class="carousel slide" data-bs-ride="carousel">
              <div class="carousel-inner">
                <div class="carousel-item active">
                  <img src="/img/hero1.jpg" class="d-block w-100" alt="Welcome">
                  <div class="carousel-caption">
                    <h5>Welcome</h5>
                    <p>Welcome to our site</p>
                    <a href="/about" class="btn">Learn More</a>
                  </div>
                </div>
                <div class="carousel-item">
                  <img src="/img/hero2.jpg" class="d-block w-100" alt="Services">
                  <div class="carousel-caption">
                    <h5>Our Services</h5>
                    <a href="/services" class="btn">Explore</a>
                  </div>
                </div>
              </div>
            </div>
            </body></html>
            """;

        var slides = SliderScraper.ExtractSlides(html, BaseUrl);

        Assert.Equal(2, slides.Count);
        Assert.Equal("/img/hero1.jpg", slides[0].ImageUrl);
        Assert.Equal("/about", slides[0].LinkUrl);
        Assert.Equal("Welcome", slides[0].Caption);
        Assert.Equal("Welcome to our site", slides[0].Description);
        Assert.Equal("/img/hero2.jpg", slides[1].ImageUrl);
        Assert.Equal("/services", slides[1].LinkUrl);
        Assert.Equal("Our Services", slides[1].Caption);
    }

    // ------------------------------------------------------------------
    // ExtractSlides — Nivo Slider patterns
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractSlides_NivoSlider_ExtractsSlides()
    {
        string html = """
            <html><body>
            <div class="nivoSlider">
              <a href="/promo1"><img src="/slides/promo1.jpg" title="Spring Sale" alt="Promo 1"></a>
              <a href="/promo2"><img src="/slides/promo2.jpg" title="Summer Deal" alt="Promo 2"></a>
              <img src="/slides/promo3.jpg" title="Fall Special" alt="Promo 3">
            </div>
            </body></html>
            """;

        var slides = SliderScraper.ExtractSlides(html, BaseUrl);

        Assert.Equal(3, slides.Count);
        Assert.Equal("/slides/promo1.jpg", slides[0].ImageUrl);
        Assert.Equal("/promo1", slides[0].LinkUrl);
        Assert.Equal("Spring Sale", slides[0].Caption);
        Assert.Equal("/slides/promo2.jpg", slides[1].ImageUrl);
        Assert.Equal("/promo2", slides[1].LinkUrl);
        Assert.Null(slides[2].LinkUrl);
    }

    // ------------------------------------------------------------------
    // ExtractSlides — Generic slider patterns
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractSlides_GenericSlider_ExtractsSlides()
    {
        string html = """
            <html><body>
            <div class="main-slider">
              <div class="item"><img src="/banner/a.jpg" alt="Banner A"></div>
              <div class="item"><img src="/banner/b.jpg" alt="Banner B"></div>
              <div class="item"><img src="/banner/c.jpg" alt="Banner C"></div>
            </div>
            </body></html>
            """;

        var slides = SliderScraper.ExtractSlides(html, BaseUrl);

        Assert.Equal(3, slides.Count);
        Assert.Equal("/banner/a.jpg", slides[0].ImageUrl);
        Assert.Equal("Banner A", slides[0].Caption);
    }

    // ------------------------------------------------------------------
    // ExtractSlides — no slider found
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractSlides_NoSlider_ReturnsEmpty()
    {
        string html = """
            <html><body>
            <div class="content">
              <h1>About Us</h1>
              <p>Some content</p>
            </div>
            </body></html>
            """;

        var slides = SliderScraper.ExtractSlides(html, BaseUrl);

        Assert.Empty(slides);
    }

    [Fact]
    public void ExtractSlides_EmptyHtml_ReturnsEmpty()
    {
        Assert.Empty(SliderScraper.ExtractSlides("", BaseUrl));
    }

    [Fact]
    public void ExtractSlides_NullHtml_ReturnsEmpty()
    {
        Assert.Empty(SliderScraper.ExtractSlides(null!, BaseUrl));
    }

    // ------------------------------------------------------------------
    // Absolute URL resolution
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractSlides_ResolvesSameOriginAbsoluteUrls()
    {
        string html = """
            <html><body>
            <div class="carousel-item active">
              <img src="https://example.com/images/hero.jpg" alt="Hero">
              <a href="https://example.com/about">Learn More</a>
            </div>
            </body></html>
            """;

        var slides = SliderScraper.ExtractSlides(html, BaseUrl);

        Assert.Single(slides);
        Assert.Equal("/images/hero.jpg", slides[0].ImageUrl);
        Assert.Equal("/about", slides[0].LinkUrl);
    }

    [Fact]
    public void ExtractSlides_PreservesExternalUrls()
    {
        string html = """
            <html><body>
            <div class="carousel-item active">
              <img src="https://cdn.other.com/images/hero.jpg" alt="Hero">
            </div>
            </body></html>
            """;

        var slides = SliderScraper.ExtractSlides(html, BaseUrl);

        Assert.Single(slides);
        Assert.Equal("https://cdn.other.com/images/hero.jpg", slides[0].ImageUrl);
    }

    // ------------------------------------------------------------------
    // Link filtering
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractSlides_IgnoresPlaceholderLinks()
    {
        string html = """
            <html><body>
            <div class="carousel-item active">
              <img src="/img/slide.jpg" alt="Slide">
              <a href="#">Learn More</a>
            </div>
            </body></html>
            """;

        var slides = SliderScraper.ExtractSlides(html, BaseUrl);

        Assert.Single(slides);
        Assert.Null(slides[0].LinkUrl);
    }

    [Fact]
    public void ExtractSlides_IgnoresJavascriptLinks()
    {
        string html = """
            <html><body>
            <div class="carousel-item active">
              <img src="/img/slide.jpg" alt="Slide">
              <a href="javascript:void(0)">Click</a>
            </div>
            </body></html>
            """;

        var slides = SliderScraper.ExtractSlides(html, BaseUrl);

        Assert.Single(slides);
        Assert.Null(slides[0].LinkUrl);
    }

    // ------------------------------------------------------------------
    // BuildSliderHtml integration with scraped slides
    // ------------------------------------------------------------------

    [Fact]
    public void BuildSliderHtml_WithScrapedSlides_UsesScrapedData()
    {
        var scraped = new List<ScrapedSlide>
        {
            new("/images/hero1.jpg", "/personal", "Personal Banking", "Great rates"),
            new("/images/hero2.jpg", "/business", "Business Banking", null),
        };

        string html = DnnXmlParser.BuildSliderHtml("Banner", [], scraped);

        // Should use scraped image URLs
        Assert.Contains("/images/hero1.jpg", html);
        Assert.Contains("/images/hero2.jpg", html);

        // Should use scraped link URLs
        Assert.Contains("href=\"/personal\"", html);
        Assert.Contains("href=\"/business\"", html);

        // Should use scraped captions
        Assert.Contains("Personal Banking", html);
        Assert.Contains("Business Banking", html);

        // Should include description when available
        Assert.Contains("Great rates", html);

        // Should NOT contain TODO placeholder for scraped slides with real links
        // (but will for slides without links)
    }

    [Fact]
    public void BuildSliderHtml_WithScrapedSlides_OverridesExportImages()
    {
        var scraped = new List<ScrapedSlide>
        {
            new("/live/hero.jpg", "/about", "About Us", null),
        };
        var exportImages = new List<string> { "FisSlider-Images/export-slide.jpg" };

        string html = DnnXmlParser.BuildSliderHtml("Banner", exportImages, scraped);

        // Should use scraped data, not export images
        Assert.Contains("/live/hero.jpg", html);
        Assert.DoesNotContain("export-slide.jpg", html);
    }

    [Fact]
    public void BuildSliderHtml_WithoutScrapedSlides_UsesExportImages()
    {
        var exportImages = new List<string> { "FisSlider-Images/slide1.jpg" };

        string html = DnnXmlParser.BuildSliderHtml("Banner", exportImages, scrapedSlides: null);

        Assert.Contains("/FisSlider-Images/slide1.jpg", html);
        Assert.Contains("<!-- TODO:", html);
    }

    [Fact]
    public void BuildSliderHtml_EmptyScrapedSlides_FallsBackToExportImages()
    {
        var exportImages = new List<string> { "FisSlider-Images/slide1.jpg" };

        string html = DnnXmlParser.BuildSliderHtml("Banner", exportImages, []);

        // Empty scraped list should fall back to export images
        Assert.Contains("/FisSlider-Images/slide1.jpg", html);
    }
}
