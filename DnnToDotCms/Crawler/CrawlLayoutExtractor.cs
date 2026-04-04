using System.Net;
using HtmlAgilityPack;

namespace DnnToDotCms.Crawler;

/// <summary>
/// Describes the layout extracted from a crawled page, containing the
/// template body HTML (with <c>#parseContainer</c> placeholders), CSS/JS
/// references, and the pane-to-slot mapping used by
/// <see cref="CrawlToBundleConverter"/> to produce DotCMS template
/// definitions.
/// </summary>
public sealed record CrawlLayout(
    /// <summary>
    /// Template body HTML with the main content area replaced by a
    /// <c>##CONTENT_PANE##</c> placeholder that the caller substitutes
    /// with a <c>#parseContainer</c> Velocity directive.
    /// </summary>
    string TemplateBody,
    /// <summary>
    /// CSS <c>&lt;link&gt;</c> tags extracted from the page
    /// <c>&lt;head&gt;</c> for inclusion in the DotCMS template header.
    /// </summary>
    string TemplateHeader,
    /// <summary>
    /// Maps pane names to unique integer slot IDs used by DotCMS
    /// <c>#parseContainer</c> directives.
    /// </summary>
    IReadOnlyDictionary<string, int> PaneMap,
    /// <summary>Theme name used for asset path resolution.</summary>
    string ThemeName);

/// <summary>
/// Analyses the full HTML of a crawled page to extract layout structure,
/// CSS/JS references, and a template skeleton.  The resulting
/// <see cref="CrawlLayout"/> is used by <see cref="CrawlToBundleConverter"/>
/// to produce DotCMS-compatible template and container definitions instead
/// of the minimal fallback template.
/// </summary>
public static class CrawlLayoutExtractor
{
    /// <summary>Placeholder replaced with <c>#parseContainer</c> by the caller.</summary>
    internal const string ContentPanePlaceholder = "##CONTENT_PANE##";

    /// <summary>
    /// Extract the layout structure from a page's full HTML.
    /// </summary>
    /// <param name="fullHtml">Complete page HTML as returned by the server.</param>
    /// <param name="themeName">
    /// Theme name used for rewriting CSS/JS paths into the
    /// <c>/application/themes/{themeName}/</c> namespace.
    /// </param>
    /// <param name="baseUrl">
    /// The base URL of the crawled site, used to resolve relative asset
    /// references and to filter out external resources.
    /// </param>
    /// <returns>
    /// A <see cref="CrawlLayout"/> with the template body, header, pane
    /// map, and theme name; or <c>null</c> when the HTML cannot be parsed
    /// into a usable layout.
    /// </returns>
    public static CrawlLayout? ExtractLayout(string fullHtml, string themeName, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(fullHtml))
            return null;

        var doc = new HtmlDocument();
        doc.LoadHtml(fullHtml);

        // Build the theme prefix so all same-origin asset references in the
        // template point to /application/themes/{themeName}/ — matching the
        // folder structure where ConvertAssets places crawled files.
        string themePrefix = BuildThemePrefix(themeName);

        // --- Extract <head> CSS/JS references ---
        string header = ExtractHeadReferences(doc, baseUrl, themePrefix);

        // --- Build template body from <body> ---
        var body = doc.DocumentNode.SelectSingleNode("//body");
        if (body is null)
            return null;

        // Locate the main content element.
        var mainNode = body.SelectSingleNode(".//main")
                    ?? body.SelectSingleNode(".//*[@role='main']");

        string templateBody;
        if (mainNode is not null)
        {
            // Replace the main content element's inner HTML with the
            // placeholder.  This preserves the surrounding layout
            // (header, footer, sidebar, etc.) as static template HTML.
            mainNode.InnerHtml = "\n  " + ContentPanePlaceholder + "\n";
            templateBody = body.InnerHtml.Trim();
        }
        else
        {
            // No semantic main element — use the whole body with a
            // placeholder so the template still contains one pane.
            templateBody = ContentPanePlaceholder;
        }

        // Remove <script> elements from the template body — they are
        // collected separately and placed as asset references.
        templateBody = RemoveScriptElements(templateBody);

        // Append JS <script> tags collected from the page.
        string scriptTags = ExtractScriptReferences(doc, baseUrl, themePrefix);
        if (!string.IsNullOrEmpty(scriptTags))
            templateBody = templateBody + "\n" + scriptTags;

        // Rewrite remaining asset paths in the template body so that
        // same-origin absolute URLs and root-relative paths point to
        // /application/themes/{themeName}/ — matching where ConvertAssets
        // places files.
        templateBody = RewriteAssetRefsInTemplate(templateBody, baseUrl, themePrefix);

        var paneMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ContentPane"] = 1,
        };

        return new CrawlLayout(templateBody, header, paneMap, themeName);
    }

    // ------------------------------------------------------------------
    // Internal helpers (internal for testing)
    // ------------------------------------------------------------------

    /// <summary>
    /// Extract <c>&lt;link rel="stylesheet"&gt;</c> tags from the
    /// <c>&lt;head&gt;</c> element, rewriting same-origin URLs to
    /// theme-prefixed paths.
    /// </summary>
    internal static string ExtractHeadReferences(HtmlDocument doc, Uri baseUrl,
        string assetPrefix = "/application/")
    {
        var head = doc.DocumentNode.SelectSingleNode("//head");
        if (head is null)
            return string.Empty;

        var links = new List<string>();
        string authority = baseUrl.GetLeftPart(UriPartial.Authority);

        foreach (var node in head.SelectNodes(".//link[@rel='stylesheet'][@href]")
                             ?? Enumerable.Empty<HtmlNode>())
        {
            string href = WebUtility.HtmlDecode(node.GetAttributeValue("href", ""));
            if (string.IsNullOrWhiteSpace(href))
                continue;

            string rewritten = RewriteSingleUrl(href, authority, assetPrefix);
            links.Add($"<link rel=\"stylesheet\" href=\"{rewritten}\">");
        }

        return string.Join("\n", links);
    }

    /// <summary>
    /// Extract external <c>&lt;script src="…"&gt;</c> tags from the
    /// page, rewriting same-origin URLs.
    /// </summary>
    internal static string ExtractScriptReferences(HtmlDocument doc, Uri baseUrl,
        string assetPrefix = "/application/")
    {
        var tags = new List<string>();
        string authority = baseUrl.GetLeftPart(UriPartial.Authority);

        foreach (var node in doc.DocumentNode.SelectNodes("//script[@src]")
                             ?? Enumerable.Empty<HtmlNode>())
        {
            string src = WebUtility.HtmlDecode(node.GetAttributeValue("src", ""));
            if (string.IsNullOrWhiteSpace(src))
                continue;

            string rewritten = RewriteSingleUrl(src, authority, assetPrefix);
            tags.Add($"<script src=\"{rewritten}\"></script>");
        }

        return string.Join("\n", tags);
    }

    /// <summary>
    /// Remove <c>&lt;script&gt;</c> elements (both external and inline)
    /// from an HTML string so they can be placed at the end of the
    /// template body instead of scattered throughout.
    /// </summary>
    internal static string RemoveScriptElements(string html)
    {
        if (string.IsNullOrEmpty(html))
            return html;

        var doc = new HtmlDocument();
        doc.LoadHtml("<body>" + html + "</body>");
        var scripts = doc.DocumentNode.SelectNodes("//script");
        if (scripts is null)
            return html;

        foreach (var s in scripts.ToList())
            s.Remove();

        var body = doc.DocumentNode.SelectSingleNode("//body");
        return body?.InnerHtml.Trim() ?? html;
    }

    /// <summary>
    /// Rewrite same-origin asset URLs in the template body HTML so they
    /// point to the given <paramref name="assetPrefix"/> (typically
    /// <c>/application/themes/{themeName}/</c>).
    /// </summary>
    internal static string RewriteAssetRefsInTemplate(string html, Uri baseUrl,
        string assetPrefix = "/application/")
    {
        if (string.IsNullOrEmpty(html))
            return html;

        string authority = baseUrl.GetLeftPart(UriPartial.Authority);

        // Rewrite absolute same-origin URLs (e.g. https://example.com/img/x.png → {prefix}img/x.png).
        if (html.Contains(authority, StringComparison.OrdinalIgnoreCase))
        {
            html = html.Replace(authority + "/", assetPrefix, StringComparison.OrdinalIgnoreCase);
        }

        // Rewrite root-relative paths in attribute contexts.
        // We use a simple regex to find href="/{path}" or src="/{path}"
        // patterns and prepend the asset prefix.  The negative lookahead
        // prevents double-rewriting any path already under /application/.
        html = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"((?:href|src|action)=[""'])/(?!application/)(?!/)",
            "$1" + assetPrefix);

        return html;
    }

    /// <summary>
    /// Build the asset prefix for a theme:
    /// <c>/application/themes/{themeName}/</c> when a theme name is
    /// provided, or <c>/application/</c> as fallback.
    /// </summary>
    internal static string BuildThemePrefix(string? themeName)
    {
        if (string.IsNullOrWhiteSpace(themeName))
            return "/application/";
        return $"/application/themes/{themeName}/";
    }

    /// <summary>
    /// Rewrite a single URL: strip the origin from absolute same-origin
    /// URLs and prepend the given <paramref name="assetPrefix"/> to
    /// root-relative paths.
    /// </summary>
    private static string RewriteSingleUrl(string url, string authority,
        string assetPrefix = "/application/")
    {
        if (url.StartsWith(authority, StringComparison.OrdinalIgnoreCase))
        {
            string path = url[authority.Length..];
            if (path.Length == 0 || path[0] != '/')
                path = "/" + path;
            return assetPrefix.TrimEnd('/') + path;
        }

        // Root-relative path on the same origin.
        if (url.StartsWith('/') && !url.StartsWith("//"))
        {
            return assetPrefix.TrimEnd('/') + url;
        }

        // External or protocol-relative — leave as-is.
        return url;
    }
}
