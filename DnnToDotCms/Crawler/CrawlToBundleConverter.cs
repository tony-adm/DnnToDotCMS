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
    /// HTML pages will be stored under.
    /// </summary>
    public static DotCmsContentType BuildHtmlContentType()
    {
        return new DotCmsContentType
        {
            Clazz       = "com.dotcms.contenttype.model.type.SimpleContentType",
            Name        = "HTMLContent",
            Variable    = "htmlContent",
            Description = "HTML content migrated from a crawled website.",
            Icon        = "fa fa-file-text",
            Fields =
            [
                new DotCmsField
                {
                    Clazz          = "com.dotcms.contenttype.model.field.ImmutableTextField",
                    Name           = "Title",
                    Variable       = "title",
                    DataType       = "TEXT",
                    FieldTypeLabel = "Text",
                    Indexed        = true,
                    Searchable     = true,
                    Listed         = true,
                    Required       = true,
                },
                new DotCmsField
                {
                    Clazz          = "com.dotcms.contenttype.model.field.ImmutableWysiwygField",
                    Name           = "Body",
                    Variable       = "body",
                    DataType       = "LONG_TEXT",
                    FieldTypeLabel = "WYSIWYG",
                    Indexed        = true,
                    Searchable     = true,
                },
            ],
        };
    }

    /// <summary>
    /// Convert each <see cref="CrawledPage"/> to a <see cref="DnnHtmlContent"/>
    /// that <see cref="Bundle.BundleWriter"/> can write as a published contentlet.
    /// </summary>
    public static IReadOnlyList<DnnHtmlContent> ConvertPages(CrawlResult crawlResult)
    {
        return crawlResult.Pages
            .Select(p => new DnnHtmlContent(
                Title:       string.IsNullOrWhiteSpace(p.Title) ? DeriveSlug(p.Url) : p.Title,
                HtmlBody:    p.HtmlBody))
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
    public static IReadOnlyList<DnnPortalFile> ConvertAssets(CrawlResult crawlResult)
    {
        return crawlResult.Assets
            .Select(a =>
            {
                string fileName = Path.GetFileName(a.RelativePath);
                string folder = Path.GetDirectoryName(a.RelativePath)?.Replace('\\', '/') ?? "";
                if (folder.Length > 0 && !folder.EndsWith('/'))
                    folder += "/";

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
}
