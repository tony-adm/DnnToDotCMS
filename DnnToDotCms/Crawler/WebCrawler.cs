using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace DnnToDotCms.Crawler;

/// <summary>
/// Crawls a live website starting from a given URL, following same-origin
/// links, and collecting HTML pages and static assets.  The crawler stays
/// within the origin of the start URL and respects a configurable page limit
/// to avoid unbounded crawls.
/// </summary>
public sealed class WebCrawler
{
    private readonly HttpClient _http;
    private readonly int _maxPages;

    /// <summary>
    /// Initialise a new <see cref="WebCrawler"/>.
    /// </summary>
    /// <param name="httpClient">
    /// An <see cref="HttpClient"/> to use for all requests.  This allows
    /// callers to configure timeouts, handlers and to inject a mock handler
    /// for testing.
    /// </param>
    /// <param name="maxPages">
    /// Maximum number of HTML pages to crawl (default 200).
    /// </param>
    public WebCrawler(HttpClient httpClient, int maxPages = 200)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _maxPages = maxPages > 0 ? maxPages : throw new ArgumentOutOfRangeException(nameof(maxPages));
    }

    /// <summary>
    /// Crawl the site starting at <paramref name="startUrl"/> and return
    /// all discovered pages and assets.
    /// </summary>
    public async Task<CrawlResult> CrawlAsync(Uri startUrl, CancellationToken cancellationToken = default)
    {
        if (!startUrl.IsAbsoluteUri)
            throw new ArgumentException("Start URL must be absolute.", nameof(startUrl));

        var baseUri = new Uri(startUrl.GetLeftPart(UriPartial.Authority));

        // Track visited URLs to avoid revisiting.
        var visitedPages  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var pageQueue = new Queue<Uri>();
        pageQueue.Enqueue(startUrl);
        visitedPages.Add(NormalizeUrl(startUrl));

        var pages  = new List<CrawledPage>();
        var assets = new List<CrawledAsset>();

        while (pageQueue.Count > 0 && pages.Count < _maxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Uri currentUrl = pageQueue.Dequeue();

            CrawledPage? page = await TryCrawlPageAsync(currentUrl, cancellationToken);
            if (page is null)
                continue;

            pages.Add(page);

            // Reuse the full HTML stored on the page to extract links and
            // assets — avoids a redundant second HTTP request.
            string fullHtml = page.FullHtml;
            if (string.IsNullOrEmpty(fullHtml))
                continue;

            var doc = new HtmlDocument();
            doc.LoadHtml(fullHtml);

            // Discover links to other same-origin pages.
            foreach (Uri link in ExtractLinks(doc, baseUri))
            {
                string key = NormalizeUrl(link);
                if (visitedPages.Add(key) && IsSameOrigin(link, baseUri))
                    pageQueue.Enqueue(link);
            }

            // Discover static asset URLs.
            // Use a queue so that sub-resources discovered inside CSS files
            // (e.g. @import, url()) are also downloaded.
            var assetQueue = new Queue<Uri>(ExtractAssetUrls(doc, baseUri));

            while (assetQueue.Count > 0)
            {
                Uri assetUrl = assetQueue.Dequeue();
                string key = NormalizeUrl(assetUrl);
                if (!visitedAssets.Add(key) || !IsSameOrigin(assetUrl, baseUri))
                    continue;

                CrawledAsset? asset = await TryDownloadAssetAsync(assetUrl, baseUri, cancellationToken);
                if (asset is null)
                    continue;

                assets.Add(asset);

                // If the asset is a CSS file, parse it for sub-resource
                // references (@import and url()) so that fonts, images,
                // and imported stylesheets are also downloaded.
                if (IsCssAsset(asset))
                {
                    foreach (Uri subUrl in ExtractCssReferences(asset.Content, asset.Url))
                        assetQueue.Enqueue(subUrl);
                }
            }
        }

        return new CrawlResult(baseUri, pages, assets);
    }

    // ----- Internal helpers (internal for testing) -----

    internal static IEnumerable<Uri> ExtractLinks(HtmlDocument doc, Uri baseUri)
    {
        var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
        if (anchors is null) yield break;

        foreach (var a in anchors)
        {
            string href = WebUtility.HtmlDecode(a.GetAttributeValue("href", ""));
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#')
                || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (Uri.TryCreate(baseUri, href, out Uri? resolved) && resolved.Scheme.StartsWith("http"))
                yield return resolved;
        }
    }

    internal static IEnumerable<Uri> ExtractAssetUrls(HtmlDocument doc, Uri baseUri)
    {
        // Images
        foreach (var node in doc.DocumentNode.SelectNodes("//img[@src]") ?? Enumerable.Empty<HtmlNode>())
        {
            string src = WebUtility.HtmlDecode(node.GetAttributeValue("src", ""));
            if (Uri.TryCreate(baseUri, src, out Uri? uri))
                yield return uri;
        }

        // CSS stylesheets
        foreach (var node in doc.DocumentNode.SelectNodes("//link[@rel='stylesheet'][@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            string href = WebUtility.HtmlDecode(node.GetAttributeValue("href", ""));
            if (Uri.TryCreate(baseUri, href, out Uri? uri))
                yield return uri;
        }

        // Scripts
        foreach (var node in doc.DocumentNode.SelectNodes("//script[@src]") ?? Enumerable.Empty<HtmlNode>())
        {
            string src = WebUtility.HtmlDecode(node.GetAttributeValue("src", ""));
            if (Uri.TryCreate(baseUri, src, out Uri? uri))
                yield return uri;
        }

        // Favicons / icons
        foreach (var node in doc.DocumentNode.SelectNodes("//link[@rel='icon' or @rel='shortcut icon'][@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            string href = WebUtility.HtmlDecode(node.GetAttributeValue("href", ""));
            if (Uri.TryCreate(baseUri, href, out Uri? uri))
                yield return uri;
        }
    }

    /// <summary>
    /// Regex matching CSS <c>@import</c> rules and <c>url()</c> references.
    /// Captures the URL from patterns like:
    /// <list type="bullet">
    /// <item><c>@import url("file.css")</c></item>
    /// <item><c>@import "file.css"</c></item>
    /// <item><c>url("image.png")</c></item>
    /// <item><c>url(image.png)</c></item>
    /// </list>
    /// Skips <c>data:</c> URIs.
    /// </summary>
    private static readonly Regex CssUrlRegex = new(
        @"(?:@import\s+)?url\(\s*[""']?(?<url>[^""')\s]+?)[""']?\s*\)|@import\s+[""'](?<import>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extract sub-resource URLs from a CSS file's content.  This discovers
    /// <c>@import</c> rules, <c>url()</c> references for fonts, background
    /// images, and other assets referenced within stylesheets.
    /// </summary>
    /// <param name="cssContent">Raw CSS file bytes.</param>
    /// <param name="cssUrl">
    /// Absolute URL of the CSS file, used to resolve relative paths.
    /// </param>
    /// <returns>Absolute URIs discovered in the CSS.</returns>
    internal static IEnumerable<Uri> ExtractCssReferences(byte[] cssContent, Uri cssUrl)
    {
        if (cssContent.Length == 0)
            yield break;

        string css;
        try
        {
            css = System.Text.Encoding.UTF8.GetString(cssContent);
        }
        catch
        {
            yield break;
        }

        foreach (Match match in CssUrlRegex.Matches(css))
        {
            string url = match.Groups["url"].Success
                ? match.Groups["url"].Value
                : match.Groups["import"].Value;

            if (string.IsNullOrWhiteSpace(url))
                continue;

            // Skip data URIs and fragment-only references.
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith('#'))
                continue;

            if (Uri.TryCreate(cssUrl, url.Trim(), out Uri? resolved)
                && (resolved.Scheme == "http" || resolved.Scheme == "https"))
                yield return resolved;
        }
    }

    /// <summary>
    /// Determine whether a crawled asset is a CSS file based on its MIME
    /// type or file extension so that its content can be parsed for
    /// sub-resource references.
    /// </summary>
    internal static bool IsCssAsset(CrawledAsset asset)
    {
        if (!string.IsNullOrEmpty(asset.MimeType)
            && asset.MimeType.Contains("css", StringComparison.OrdinalIgnoreCase))
            return true;

        return asset.RelativePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ExtractTitle(HtmlDocument doc)
    {
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        return titleNode is not null ? WebUtility.HtmlDecode(titleNode.InnerText).Trim() : string.Empty;
    }

    internal static string ExtractDescription(HtmlDocument doc)
    {
        var metaDesc = doc.DocumentNode.SelectSingleNode(
            "//meta[@name='description']") ??
            doc.DocumentNode.SelectSingleNode(
            "//meta[@name='Description']");
        return metaDesc is not null
            ? WebUtility.HtmlDecode(metaDesc.GetAttributeValue("content", "")).Trim()
            : string.Empty;
    }

    internal static string ExtractBodyContent(HtmlDocument doc)
    {
        // Prefer <main> or role="main" for primary content.
        var main = doc.DocumentNode.SelectSingleNode("//main")
                ?? doc.DocumentNode.SelectSingleNode("//*[@role='main']");
        if (main is not null)
            return main.InnerHtml.Trim();

        var body = doc.DocumentNode.SelectSingleNode("//body");
        return body is not null ? body.InnerHtml.Trim() : string.Empty;
    }

    // ----- Private helpers -----

    private async Task<CrawledPage?> TryCrawlPageAsync(Uri url, CancellationToken ct)
    {
        try
        {
            string html = await FetchHtmlAsync(url, ct);
            if (string.IsNullOrEmpty(html))
                return null;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            string title = ExtractTitle(doc);
            string description = ExtractDescription(doc);
            string body = ExtractBodyContent(doc);

            if (string.IsNullOrWhiteSpace(body))
                return null;

            return new CrawledPage(url, title, description, body, html);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<string> FetchHtmlAsync(Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/html");

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return string.Empty;

        string? contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<CrawledAsset?> TryDownloadAssetAsync(Uri url, Uri baseUri, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            byte[] content = await response.Content.ReadAsByteArrayAsync(ct);
            if (content.Length == 0)
                return null;

            string mime = response.Content.Headers.ContentType?.MediaType ?? "";
            string relativePath = DeriveRelativePath(url, baseUri);

            return new CrawledAsset(url, relativePath, mime, content);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    private static bool IsSameOrigin(Uri candidate, Uri baseUri)
        => string.Equals(candidate.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
           && candidate.Port == baseUri.Port
           && candidate.Scheme == baseUri.Scheme;

    internal static string NormalizeUrl(Uri uri)
    {
        // Drop fragment and trailing slash for dedup.
        var builder = new UriBuilder(uri) { Fragment = "" };
        string path = builder.Path.TrimEnd('/');
        return $"{builder.Scheme}://{builder.Host}:{builder.Port}{path}{builder.Query}".ToLowerInvariant();
    }

    internal static string DeriveRelativePath(Uri assetUrl, Uri baseUri)
    {
        // Use the URL path relative to the origin as the file path.
        string path = assetUrl.AbsolutePath.TrimStart('/');

        // Remove query strings from the path.
        int qIndex = path.IndexOf('?');
        if (qIndex >= 0)
            path = path[..qIndex];

        // If the path is empty, derive a name from the host.
        if (string.IsNullOrWhiteSpace(path))
            path = "index";

        return path;
    }
}
