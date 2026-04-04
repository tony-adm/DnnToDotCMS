using System.Net;
using HtmlAgilityPack;

namespace DnnToDotCms.Crawler;

/// <summary>
/// Represents a single slide extracted from a live website's slider/carousel.
/// </summary>
public sealed record ScrapedSlide(
    /// <summary>Image URL (absolute or root-relative).</summary>
    string ImageUrl,
    /// <summary>Optional link destination for the slide.</summary>
    string? LinkUrl,
    /// <summary>Optional caption or heading text.</summary>
    string? Caption,
    /// <summary>Optional description text.</summary>
    string? Description);

/// <summary>
/// Scrapes slider/carousel data from a live DNN website.  This supplements
/// the DNN export which does not include FisSlider slide metadata (link URLs,
/// descriptions, ordering) because it is stored in a custom SQL table.
/// </summary>
public static class SliderScraper
{
    /// <summary>
    /// Fetch a page and extract all slider/carousel slide data found in
    /// the rendered HTML.  Supports common DNN slider patterns including
    /// FisSlider, Nivo Slider, and Bootstrap carousels.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for the request.</param>
    /// <param name="pageUrl">URL of the page to scrape.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A list of <see cref="ScrapedSlide"/> records found on the page,
    /// or an empty list when none are detected.
    /// </returns>
    public static async Task<IReadOnlyList<ScrapedSlide>> ScrapeAsync(
        HttpClient httpClient, Uri pageUrl,
        CancellationToken cancellationToken = default)
    {
        string html;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
            var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            html = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Warning: Could not fetch {pageUrl} for slider scraping: {ex.Message}");
            return [];
        }

        return ExtractSlides(html, pageUrl);
    }

    /// <summary>
    /// Extract slider/carousel slides from page HTML.
    /// Searches for multiple common slider markup patterns.
    /// </summary>
    internal static IReadOnlyList<ScrapedSlide> ExtractSlides(string html, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        string authority = baseUrl.GetLeftPart(UriPartial.Authority);

        // Try multiple slider patterns in order of specificity.
        var slides = ExtractFisSliderSlides(doc, authority);
        if (slides.Count > 0)
            return slides;

        slides = ExtractBootstrapCarouselSlides(doc, authority);
        if (slides.Count > 0)
            return slides;

        slides = ExtractNivoSliderSlides(doc, authority);
        if (slides.Count > 0)
            return slides;

        slides = ExtractGenericSliderSlides(doc, authority);
        return slides;
    }

    /// <summary>
    /// Extract slides from FisSlider-specific markup.
    /// FisSlider typically renders a <c>.fisSlider</c> or <c>.fis-slider</c>
    /// container with individual slide divs containing images and links.
    /// </summary>
    internal static IReadOnlyList<ScrapedSlide> ExtractFisSliderSlides(
        HtmlDocument doc, string authority)
    {
        // FisSlider renders slide items inside a container with class
        // containing "slider" or "slide".  Each slide typically has an
        // <img> and optionally an <a> wrapper and caption text.
        var sliderContainers = doc.DocumentNode.SelectNodes(
            "//*[contains(@class,'fisSlider') or contains(@class,'fis-slider') or contains(@class,'FisSlider')]");
        if (sliderContainers is null)
            return [];

        var slides = new List<ScrapedSlide>();
        foreach (var container in sliderContainers)
        {
            // Each slide item is a direct child or a div with slide-related class.
            var slideItems = container.SelectNodes(
                ".//*[contains(@class,'slide')] | .//li");
            if (slideItems is null)
                continue;

            foreach (var item in slideItems)
            {
                var slide = ExtractSlideFromNode(item, authority);
                if (slide is not null)
                    slides.Add(slide);
            }
        }

        return slides;
    }

    /// <summary>
    /// Extract slides from Bootstrap carousel markup.
    /// </summary>
    internal static IReadOnlyList<ScrapedSlide> ExtractBootstrapCarouselSlides(
        HtmlDocument doc, string authority)
    {
        var carouselItems = doc.DocumentNode.SelectNodes(
            "//*[contains(@class,'carousel-item')]");
        if (carouselItems is null)
            return [];

        var slides = new List<ScrapedSlide>();
        foreach (var item in carouselItems)
        {
            var slide = ExtractSlideFromNode(item, authority);
            if (slide is not null)
                slides.Add(slide);
        }

        return slides;
    }

    /// <summary>
    /// Extract slides from Nivo Slider markup (common in DNN sites).
    /// </summary>
    internal static IReadOnlyList<ScrapedSlide> ExtractNivoSliderSlides(
        HtmlDocument doc, string authority)
    {
        var nivoContainer = doc.DocumentNode.SelectSingleNode(
            "//*[contains(@class,'nivoSlider') or contains(@class,'nivo-slider')]");
        if (nivoContainer is null)
            return [];

        // Nivo Slider uses direct <img> children or <a><img></a> patterns.
        var images = nivoContainer.SelectNodes(".//img[@src]");
        if (images is null)
            return [];

        var slides = new List<ScrapedSlide>();
        foreach (var img in images)
        {
            string? imgSrc = ResolveUrl(
                WebUtility.HtmlDecode(img.GetAttributeValue("src", "")), authority);
            if (string.IsNullOrEmpty(imgSrc))
                continue;

            string? linkUrl = null;
            var parentLink = img.ParentNode;
            if (parentLink?.Name == "a")
            {
                linkUrl = ResolveUrl(
                    WebUtility.HtmlDecode(parentLink.GetAttributeValue("href", "")),
                    authority);
            }

            string? caption = WebUtility.HtmlDecode(
                img.GetAttributeValue("title", "")!);
            if (string.IsNullOrEmpty(caption))
                caption = WebUtility.HtmlDecode(img.GetAttributeValue("alt", "")!);
            if (string.IsNullOrEmpty(caption))
                caption = null;

            slides.Add(new ScrapedSlide(imgSrc, linkUrl, caption, Description: null));
        }

        return slides;
    }

    /// <summary>
    /// Generic fallback: look for any container with "slider" or "slideshow"
    /// in its class that contains multiple images.
    /// </summary>
    internal static IReadOnlyList<ScrapedSlide> ExtractGenericSliderSlides(
        HtmlDocument doc, string authority)
    {
        var containers = doc.DocumentNode.SelectNodes(
            "//*[contains(@class,'slider') or contains(@class,'slideshow') or contains(@class,'Slider') or contains(@class,'Slideshow')]");
        if (containers is null)
            return [];

        var slides = new List<ScrapedSlide>();

        foreach (var container in containers)
        {
            var images = container.SelectNodes(".//img[@src]");
            if (images is null || images.Count < 2)
                continue; // Need at least 2 images to consider it a slider

            foreach (var img in images)
            {
                var slide = ExtractSlideFromNode(img.ParentNode ?? img, authority);
                if (slide is not null)
                    slides.Add(slide);
            }

            if (slides.Count > 0)
                break; // Use the first slider found
        }

        return slides;
    }

    /// <summary>
    /// Extract a single slide from an HTML node, looking for an image,
    /// optional link, and optional caption text.
    /// </summary>
    private static ScrapedSlide? ExtractSlideFromNode(HtmlNode node, string authority)
    {
        // Find the slide image.
        var img = node.Name == "img" ? node : node.SelectSingleNode(".//img[@src]");
        if (img is null)
            return null;

        string? imgSrc = ResolveUrl(
            WebUtility.HtmlDecode(img.GetAttributeValue("src", "")), authority);
        if (string.IsNullOrEmpty(imgSrc))
            return null;

        // Find the slide link.
        string? linkUrl = null;
        var link = node.SelectSingleNode(".//a[@href]")
                ?? (node.ParentNode?.Name == "a" ? node.ParentNode : null);
        if (link is not null)
        {
            string href = WebUtility.HtmlDecode(link.GetAttributeValue("href", ""));
            if (!string.IsNullOrEmpty(href) && href != "#" && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                linkUrl = ResolveUrl(href, authority);
            }
        }

        // Find caption/heading text.
        string? caption = null;
        var heading = node.SelectSingleNode(".//h1 | .//h2 | .//h3 | .//h4 | .//h5 | .//h6");
        if (heading is not null)
            caption = WebUtility.HtmlDecode(heading.InnerText.Trim());

        // Fall back to alt text if no heading found.
        if (string.IsNullOrEmpty(caption))
        {
            string altVal = img.GetAttributeValue("alt", "");
            caption = string.IsNullOrWhiteSpace(altVal) ? null : WebUtility.HtmlDecode(altVal)?.Trim();
        }

        // Find description text (paragraph after heading).
        string? description = null;
        var paragraph = node.SelectSingleNode(".//p");
        if (paragraph is not null)
        {
            string text = WebUtility.HtmlDecode(paragraph.InnerText.Trim());
            if (!string.IsNullOrEmpty(text))
                description = text;
        }

        return new ScrapedSlide(imgSrc, linkUrl, caption, description);
    }

    /// <summary>
    /// Resolve a URL: strip the site authority if present (to get a
    /// root-relative path) or return as-is for external URLs.
    /// </summary>
    private static string? ResolveUrl(string? url, string authority)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        // Strip same-origin authority to get root-relative path.
        if (url.StartsWith(authority, StringComparison.OrdinalIgnoreCase))
        {
            string path = url[authority.Length..];
            return path.Length == 0 ? "/" : path;
        }

        return url;
    }
}
