namespace DnnToDotCms.Crawler;

/// <summary>
/// Represents a single page discovered and downloaded during a site crawl.
/// </summary>
public sealed record CrawledPage(
    /// <summary>Absolute URL of the page.</summary>
    Uri Url,
    /// <summary>Page title extracted from the &lt;title&gt; element.</summary>
    string Title,
    /// <summary>Meta description extracted from the page.</summary>
    string Description,
    /// <summary>
    /// HTML body content.  When a <c>&lt;main&gt;</c> or
    /// <c>role="main"</c> element exists its inner HTML is used;
    /// otherwise the full <c>&lt;body&gt;</c> inner HTML is used.
    /// </summary>
    string HtmlBody);

/// <summary>
/// Represents a static asset (image, CSS, JS, font, etc.) downloaded during
/// a site crawl.
/// </summary>
public sealed record CrawledAsset(
    /// <summary>Absolute URL of the asset.</summary>
    Uri Url,
    /// <summary>
    /// Site-relative path derived from the URL, e.g. <c>images/logo.png</c>.
    /// </summary>
    string RelativePath,
    /// <summary>MIME type reported by the server (may be empty).</summary>
    string MimeType,
    /// <summary>Raw file bytes.</summary>
    byte[] Content);

/// <summary>
/// Aggregate result of a full site crawl, containing all discovered pages
/// and static assets.
/// </summary>
public sealed record CrawlResult(
    /// <summary>The base URL that the crawl started from.</summary>
    Uri BaseUrl,
    /// <summary>All HTML pages discovered on the site.</summary>
    IReadOnlyList<CrawledPage> Pages,
    /// <summary>All static assets downloaded from the site.</summary>
    IReadOnlyList<CrawledAsset> Assets);
