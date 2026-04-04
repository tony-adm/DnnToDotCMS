using DnnToDotCms.Models;

namespace DnnToDotCms.Crawler;

/// <summary>
/// Converts the result of a web crawl into the DotCMS models that
/// <see cref="Bundle.BundleWriter"/> expects, so that the existing bundle
/// generation pipeline can be reused without modification.
/// </summary>
public static class CrawlToBundleConverter
{
    /// <summary>
    /// Build the single <c>htmlContent</c> content type that the crawled
    /// HTML pages will be stored under.  The field structure matches
    /// <c>ModuleMappings.HtmlContent()</c> (Title, Body, Image) so that
    /// crawl-produced bundles have the same content-type shape as
    /// export-produced bundles.
    /// </summary>
    public static DotCmsContentType BuildHtmlContentType()
    {
        return new DotCmsContentType
        {
            Clazz       = "com.dotcms.contenttype.model.type.SimpleContentType",
            Name        = "HTMLContent",
            Variable    = "htmlContent",
            Description = "Converted from DNN HTML module",
            Icon        = "fa fa-code",
            Fields =
            [
                new DotCmsField
                {
                    Clazz          = "com.dotcms.contenttype.model.field.TextField",
                    Name           = "Title",
                    Variable       = "title",
                    DataType       = "TEXT",
                    FieldTypeLabel = "Text",
                    Indexed        = true,
                    Searchable     = true,
                    Sortable       = true,
                    Listed         = true,
                    Required       = true,
                },
                new DotCmsField
                {
                    Clazz          = "com.dotcms.contenttype.model.field.WysiwygField",
                    Name           = "Body",
                    Variable       = "body",
                    DataType       = "LONG_TEXT",
                    FieldTypeLabel = "WYSIWYG",
                    Indexed        = true,
                    Searchable     = true,
                    Required       = true,
                },
                new DotCmsField
                {
                    Clazz          = "com.dotcms.contenttype.model.field.TextField",
                    Name           = "Image",
                    Variable       = "image",
                    DataType       = "TEXT",
                    FieldTypeLabel = "Text",
                    Indexed        = true,
                    Searchable     = true,
                    Sortable       = true,
                    Hint           = "Icon or image URL for container rendering",
                },
            ],
        };
    }

    /// <summary>
    /// Convert crawled pages into paired <see cref="DnnHtmlContent"/> and
    /// <see cref="DnnPortalPage"/> lists that share the same
    /// <c>TabUniqueId</c> / <c>UniqueId</c>, so that
    /// <see cref="Bundle.BundleWriter"/> can link content items to their
    /// pages via <c>multiTree</c> entries — matching the export path.
    /// </summary>
    /// <param name="crawlResult">The crawl result.</param>
    /// <param name="themeName">
    /// Optional theme name. When provided, asset references in HTML bodies
    /// are rewritten to <c>/application/themes/{themeName}/</c> instead of
    /// <c>/application/</c>.
    /// </param>
    public static (IReadOnlyList<DnnHtmlContent> HtmlContents, IReadOnlyList<DnnPortalPage> PortalPages) Convert(CrawlResult crawlResult, string? themeName = null)
    {
        var htmlContents = new List<DnnHtmlContent>(crawlResult.Pages.Count);
        var portalPages  = new List<DnnPortalPage>(crawlResult.Pages.Count);

        foreach (CrawledPage page in crawlResult.Pages)
        {
            // Generate a shared tab identifier so BundleWriter can link
            // the content item to its page via multiTree.
            string tabUniqueId = Guid.NewGuid().ToString();
            string slug  = DeriveSlug(page.Url);
            string title = string.IsNullOrWhiteSpace(page.Title) ? slug : page.Title;

            // Rewrite asset references in the HTML body so they point to
            // the correct application folder where crawled assets are placed.
            string rewrittenBody = RewriteAssetPaths(page.HtmlBody, crawlResult, themeName);

            htmlContents.Add(new DnnHtmlContent(
                Title:       title,
                HtmlBody:    rewrittenBody,
                TabUniqueId: tabUniqueId,
                PaneName:    "ContentPane"));

            portalPages.Add(new DnnPortalPage(
                UniqueId:    tabUniqueId,
                Name:        slug,
                Title:       title,
                Description: page.Description,
                TabPath:     "//" + slug,
                Level:       0,
                IsVisible:   true,
                SkinSrc:     ""));
        }

        return (htmlContents, portalPages);
    }

    /// <summary>
    /// Convert each <see cref="CrawledPage"/> to a <see cref="DnnHtmlContent"/>
    /// that <see cref="Bundle.BundleWriter"/> can write as a published contentlet.
    /// Each content item is linked to a page via <c>TabUniqueId</c> and placed
    /// in the <c>ContentPane</c> pane.
    /// </summary>
    /// <param name="crawlResult">The crawl result.</param>
    /// <param name="portalPages">
    /// Portal pages produced by <see cref="ConvertPortalPages"/> from the same
    /// crawl result.  The <c>TabUniqueId</c> of each content item is set to the
    /// matching page's <c>UniqueId</c> (same positional index).
    /// </param>
    /// <param name="themeName">
    /// Optional theme name. When provided, asset references in HTML bodies
    /// are rewritten to <c>/application/themes/{themeName}/</c>.
    /// </param>
    public static IReadOnlyList<DnnHtmlContent> ConvertPages(
        CrawlResult crawlResult,
        IReadOnlyList<DnnPortalPage>? portalPages = null,
        string? themeName = null)
    {
        if (portalPages is not null && portalPages.Count != crawlResult.Pages.Count)
            throw new ArgumentException(
                $"portalPages count ({portalPages.Count}) must match crawlResult.Pages count ({crawlResult.Pages.Count}).",
                nameof(portalPages));

        return crawlResult.Pages
            .Select((p, i) =>
            {
                string tabUniqueId = portalPages is not null
                    ? portalPages[i].UniqueId
                    : "";
                return new DnnHtmlContent(
                    Title:       string.IsNullOrWhiteSpace(p.Title) ? DeriveSlug(p.Url) : p.Title,
                    HtmlBody:    RewriteAssetPaths(p.HtmlBody, crawlResult, themeName),
                    TabUniqueId: tabUniqueId,
                    PaneName:    "ContentPane");
            })
            .ToList();
    }

    /// <summary>
    /// Convert each <see cref="CrawledPage"/> to a <see cref="DnnPortalPage"/>
    /// that <see cref="Bundle.BundleWriter"/> can write as an
    /// <c>htmlpageasset</c> contentlet.
    /// </summary>
    public static IReadOnlyList<DnnPortalPage> ConvertPortalPages(CrawlResult crawlResult)
    {
        return crawlResult.Pages
            .Select(p =>
            {
                string slug = DeriveSlug(p.Url);
                string title = string.IsNullOrWhiteSpace(p.Title) ? slug : p.Title;
                return new DnnPortalPage(
                    UniqueId:    Guid.NewGuid().ToString(),
                    Name:        slug,
                    Title:       title,
                    Description: p.Description,
                    TabPath:     "//" + slug,
                    Level:       0,
                    IsVisible:   true,
                    SkinSrc:     "");
            })
            .ToList();
    }

    /// <summary>
    /// Convert each <see cref="CrawledAsset"/> to a <see cref="DnnPortalFile"/>
    /// that <see cref="Bundle.BundleWriter"/> can write as a <c>FileAsset</c>
    /// contentlet.
    /// </summary>
    /// <param name="crawlResult">The crawl result.</param>
    /// <param name="themeName">
    /// Optional theme name. When provided, assets are placed under
    /// <c>application/themes/{themeName}/</c> instead of <c>application/</c>.
    /// </param>
    public static IReadOnlyList<DnnPortalFile> ConvertAssets(CrawlResult crawlResult, string? themeName = null)
    {
        string folderRoot = string.IsNullOrWhiteSpace(themeName)
            ? "application/"
            : $"application/themes/{themeName}/";

        return crawlResult.Assets
            .Select(a =>
            {
                string fileName = Path.GetFileName(a.RelativePath);
                string folder = Path.GetDirectoryName(a.RelativePath)?.Replace('\\', '/') ?? "";
                if (folder.Length > 0 && !folder.EndsWith('/'))
                    folder += "/";

                // Place all crawled assets inside the theme folder so that
                // the DotCMS bundle has proper theme structure matching the
                // export path convention.
                folder = folderRoot + folder;

                return new DnnPortalFile(
                    UniqueId:    Guid.NewGuid().ToString(),
                    VersionGuid: Guid.NewGuid().ToString(),
                    FileName:    fileName,
                    FolderPath:  folder,
                    MimeType:    a.MimeType,
                    Content:     a.Content);
            })
            .ToList();
    }

    /// <summary>
    /// Rewrite asset URL references in the HTML body so they point to
    /// the correct folder where <see cref="ConvertAssets"/> places files.
    /// When <paramref name="themeName"/> is provided, paths use
    /// <c>/application/themes/{themeName}/{relativePath}</c>; otherwise
    /// they use <c>/application/{relativePath}</c>.
    /// </summary>
    internal static string RewriteAssetPaths(string html, CrawlResult crawlResult, string? themeName = null)
    {
        if (string.IsNullOrEmpty(html) || crawlResult.Assets.Count == 0)
            return html;

        string prefix = string.IsNullOrWhiteSpace(themeName)
            ? "/application/"
            : $"/application/themes/{themeName}/";

        string baseAuthority = crawlResult.BaseUrl.GetLeftPart(UriPartial.Authority);

        foreach (CrawledAsset asset in crawlResult.Assets)
        {
            string relPath = asset.RelativePath;
            if (string.IsNullOrEmpty(relPath))
                continue;

            // Replace absolute URLs from the same origin.
            // e.g. https://example.com/images/logo.png → {prefix}images/logo.png
            string absoluteUrl = baseAuthority + "/" + relPath;
            html = html.Replace(absoluteUrl, prefix + relPath, StringComparison.OrdinalIgnoreCase);

            // Replace root-relative references.
            // e.g. /images/logo.png → {prefix}images/logo.png
            // Only replace when preceded by a delimiter that indicates an
            // HTML attribute value or CSS url() to avoid false positives.
            string rootRelative = "/" + relPath;
            html = ReplacePrefixed(html, rootRelative, prefix + relPath);
        }

        return html;
    }

    /// <summary>
    /// Replace occurrences of <paramref name="oldValue"/> with
    /// <paramref name="newValue"/> only when preceded by a character
    /// that indicates an HTML attribute value or CSS <c>url()</c>
    /// context: <c>"</c>, <c>'</c>, <c>(</c>, or <c>=</c>.
    /// </summary>
    private static string ReplacePrefixed(string html, string oldValue, string newValue)
    {
        int idx = 0;
        while (true)
        {
            int pos = html.IndexOf(oldValue, idx, StringComparison.OrdinalIgnoreCase);
            if (pos < 0)
                break;

            if (pos > 0)
            {
                char prev = html[pos - 1];
                if (prev is '"' or '\'' or '(' or '=')
                {
                    html = string.Concat(html.AsSpan(0, pos), newValue, html.AsSpan(pos + oldValue.Length));
                    idx = pos + newValue.Length;
                    continue;
                }
            }

            idx = pos + oldValue.Length;
        }

        return html;
    }

    /// <summary>
    /// Derive a URL-safe slug from the path portion of a page URL.
    /// </summary>
    internal static string DeriveSlug(Uri url)
    {
        string path = url.AbsolutePath.Trim('/');
        if (string.IsNullOrWhiteSpace(path))
            return "home";

        // Replace path separators with hyphens and normalise.
        return path
            .Replace('/', '-')
            .Replace(' ', '-')
            .ToLowerInvariant();
    }

    // ------------------------------------------------------------------
    // Theme / container / template definitions from crawled layout
    // ------------------------------------------------------------------

    /// <summary>
    /// Build container definitions from a crawled layout.  The container
    /// uses <c>$!{dotContent.body}</c> Velocity syntax to render content.
    /// </summary>
    /// <param name="themeName">
    /// Theme name to associate with the container (may be empty).
    /// </param>
    /// <returns>
    /// A list of container tuples in the format consumed by
    /// <see cref="Bundle.BundleWriter.Write"/>.
    /// </returns>
    public static IReadOnlyList<(string id, string inode, string name, string html, string themeName)>
        BuildContainerDefs(string themeName)
    {
        string id    = Guid.NewGuid().ToString();
        string inode = Guid.NewGuid().ToString();
        return [(id, inode, "Standard", "$!{dotContent.body}", themeName)];
    }

    /// <summary>
    /// Build a template definition from a <see cref="CrawlLayout"/> extracted
    /// from the first crawled page.  The layout's
    /// <see cref="CrawlLayoutExtractor.ContentPanePlaceholder"/> is replaced
    /// with a <c>#parseContainer</c> Velocity directive referencing the
    /// supplied container.
    /// </summary>
    /// <param name="layout">Layout extracted by <see cref="CrawlLayoutExtractor"/>.</param>
    /// <param name="containerId">
    /// Container identifier to embed in the <c>#parseContainer</c> directive.
    /// </param>
    /// <returns>
    /// A list containing a single template tuple in the format consumed by
    /// <see cref="Bundle.BundleWriter.Write"/>.
    /// </returns>
    public static IReadOnlyList<(string id, string inode, string name, string html,
        string header, string themeName, IReadOnlyDictionary<string, int> paneUuidMap)>
        BuildTemplateDef(CrawlLayout layout, string containerId)
    {
        string templateBody = layout.TemplateBody.Replace(
            CrawlLayoutExtractor.ContentPanePlaceholder,
            $"#parseContainer('{containerId}', '1')");

        string id    = Guid.NewGuid().ToString();
        string inode = Guid.NewGuid().ToString();

        return [(id, inode, "Default", templateBody, layout.TemplateHeader,
                 layout.ThemeName, layout.PaneMap)];
    }
}
