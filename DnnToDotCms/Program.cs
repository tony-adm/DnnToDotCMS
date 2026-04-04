using DnnToDotCms.Bundle;
using DnnToDotCms.Converter;
using DnnToDotCms.Crawler;
using DnnToDotCms.Models;
using DnnToDotCms.Parser;

// ---------------------------------------------------------------------------
// DNN → DotCMS Converter  —  CLI entry point
// ---------------------------------------------------------------------------
//
// Usage:
//   DnnToDotCms <input> [--output <site.tar.gz>] [--help]
//   DnnToDotCms --crawl <url> [--output <site.tar.gz>] [--max-pages <n>]
//   DnnToDotCms --help
//
// Arguments:
//   <input>                  Path to a DNN export folder, the export.json manifest
//                            inside that folder, or a DNN XML file (.dnn / IPortable)
//
// Options:
//   --crawl <url>            Crawl a live website and create a bundle from its content
//   --max-pages <n>          Maximum pages to crawl (default: 200, only with --crawl)
//   --output <path>          Write the bundle to a file (default: site.tar.gz)
//   --help, -h               Show this help and exit
// ---------------------------------------------------------------------------

if (args.Length == 0 || args.Any(a => a is "--help" or "-h"))
{
    PrintUsage();
    return 0;
}

// Parse arguments
string? inputPath  = null;
string? outputPath = null;
string? crawlUrl   = null;
int     maxPages   = 200;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output" when i + 1 < args.Length:
            outputPath = args[++i];
            break;
        case "--crawl" when i + 1 < args.Length:
            crawlUrl = args[++i];
            break;
        case "--max-pages" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out maxPages) || maxPages < 1)
            {
                Console.Error.WriteLine("Error: --max-pages must be a positive integer.");
                return 1;
            }
            break;
        default:
            if (!args[i].StartsWith("--"))
                inputPath = args[i];
            else
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                PrintUsage();
                return 1;
            }
            break;
    }
}

// --crawl mode: crawl a live website and produce a bundle.
if (crawlUrl is not null)
    return await RunCrawlModeAsync(crawlUrl, maxPages, outputPath ?? "site.tar.gz");

if (string.IsNullOrWhiteSpace(inputPath))
{
    Console.Error.WriteLine("Error: No input path specified.");
    PrintUsage();
    return 1;
}

bool isDirectory      = Directory.Exists(inputPath);
bool isFile           = !isDirectory && File.Exists(inputPath);
bool isExportManifest = isFile &&
    Path.GetFileName(inputPath).Equals("export.json", StringComparison.OrdinalIgnoreCase);

if (!isDirectory && !isFile)
{
    Console.Error.WriteLine($"Error: Input path not found: {inputPath}");
    return 1;
}

// Default output file name.
outputPath ??= "site.tar.gz";

try
{
    // Parse DNN export (folder, export.json manifest, or XML file)
    IReadOnlyList<DnnModule> modules = isDirectory
        ? DnnXmlParser.ParseExportFolder(inputPath)
        : isExportManifest
            ? DnnXmlParser.ParseExportJson(inputPath)
            : DnnXmlParser.ParseFile(inputPath);

    if (modules.Count == 0)
    {
        Console.Error.WriteLine("Warning: No DNN modules found in the input file.");
        return 0;
    }

    // Convert to DotCMS content types
    IReadOnlyList<DotCmsContentType> contentTypes = DnnConverter.ConvertAll(modules);

    // Locate optional themes zip (present when input is a DNN export folder).
    string? exportDir = isDirectory
        ? inputPath
        : isExportManifest
            ? Path.GetDirectoryName(inputPath)
            : null;

    string? themesZip = exportDir is not null
        ? Path.Combine(exportDir, "export_themes.zip")
        : null;

    if (themesZip is not null && !File.Exists(themesZip))
        themesZip = null;

    // Read the portal / site name from export.json when available.
    string? portalName = exportDir is not null
        ? DnnXmlParser.ParsePortalName(exportDir)
        : isExportManifest
            ? DnnXmlParser.ParsePortalName(inputPath)
            : null;

    // Extract HTML module content from the LiteDB database (export_db.zip).
    // Available only when the input is a DNN official site-export folder.
    IReadOnlyList<DnnHtmlContent> htmlContents = exportDir is not null
        ? DnnXmlParser.ParseHtmlContents(exportDir)
        : [];

    // Extract portal pages (tabs) from the LiteDB database.
    IReadOnlyList<DnnPortalPage> portalPages = exportDir is not null
        ? DnnXmlParser.ParsePortalPages(exportDir)
        : [];

    // Extract portal static files from export_files.zip + LiteDB metadata.
    IReadOnlyList<DnnPortalFile> portalFiles = exportDir is not null
        ? DnnXmlParser.ParsePortalFiles(exportDir)
        : [];

    // Delete any existing output file before writing so a failed run never
    // leaves a stale bundle from a previous successful run in place.
    if (File.Exists(outputPath))
        File.Delete(outputPath);

    // Write the DotCMS site bundle.
    using (var outStream = File.Create(outputPath))
        BundleWriter.Write(contentTypes, outStream, themesZip, portalName, htmlContents,
            portalPages, portalFiles);

    string themeNote = themesZip is not null
        ? " Containers, templates, and static theme assets included from export_themes.zip."
        : string.Empty;

    string siteNote = portalName is not null
        ? $" Site '{portalName}' will be created on import."
        : string.Empty;

    string contentNote = htmlContents.Count > 0
        ? $" {htmlContents.Count} HTML content item(s) included."
        : string.Empty;

    // Count only the Level-0 non-Admin pages that are actually written to the bundle.
    int importedPageCount = portalPages.Count(p =>
        p.Level == 0 && !p.Name.Equals("Admin", StringComparison.OrdinalIgnoreCase));
    string pageNote = importedPageCount > 0
        ? $" {importedPageCount} page(s) included."
        : string.Empty;

    string fileNote = portalFiles.Count > 0
        ? $" {portalFiles.Count} static file(s) included."
        : string.Empty;

    Console.WriteLine(
        $"Converted {modules.Count} module(s) to {contentTypes.Count} content type(s)." +
        $"{themeNote}{siteNote}{contentNote}{pageNote}{fileNote} Bundle written to: {outputPath}");

    return 0;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Error parsing DNN XML: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Unexpected error: {ex.Message}");
    return 1;
}

// ---------------------------------------------------------------------------

static async Task<int> RunCrawlModeAsync(string url, int maxPages, string outputPath)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? startUri)
        || (startUri.Scheme != "http" && startUri.Scheme != "https"))
    {
        Console.Error.WriteLine($"Error: Invalid URL: {url}");
        return 1;
    }

    try
    {
        Console.WriteLine($"Crawling {startUri} (max {maxPages} pages)…");

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "DnnToDotCms-Crawler/1.0 (+https://github.com/tony-adm/DnnToDotCMS)");
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        var crawler = new WebCrawler(httpClient, maxPages);
        CrawlResult result = await crawler.CrawlAsync(startUri);

        Console.WriteLine(
            $"Crawl complete: {result.Pages.Count} page(s), {result.Assets.Count} asset(s).");

        if (result.Pages.Count == 0)
        {
            Console.Error.WriteLine("Warning: No pages were crawled. No bundle produced.");
            return 0;
        }

        // Build content types, HTML contents, pages, and file assets from the crawl.
        // Use the paired Convert() method so that content items are linked to their
        // pages via shared TabUniqueId — matching the export-folder path.
        var contentTypes = new List<DotCmsContentType> { CrawlToBundleConverter.BuildHtmlContentType() };
        var (htmlContents, portalPages) = CrawlToBundleConverter.Convert(result);
        IReadOnlyList<DnnPortalFile> portalFiles = CrawlToBundleConverter.ConvertAssets(result);

        // Derive a site name from the hostname.
        string siteName = startUri.Host;

        // Extract layout structure from the first crawled page so that
        // the generated bundle includes a proper DotCMS theme, template,
        // and container derived from the original site's HTML structure
        // rather than a minimal fallback template.
        string themeName = BundleWriter.SanitizeHostname(siteName);
        IReadOnlyList<(string id, string inode, string name, string html, string themeName)>? containerDefs = null;
        IReadOnlyList<(string id, string inode, string name, string html, string header, string themeName, IReadOnlyDictionary<string, int> paneUuidMap)>? templateDefs = null;

        CrawledPage firstPage = result.Pages[0];
        if (!string.IsNullOrEmpty(firstPage.FullHtml))
        {
            CrawlLayout? layout = CrawlLayoutExtractor.ExtractLayout(
                firstPage.FullHtml, themeName, result.BaseUrl);
            if (layout is not null)
            {
                containerDefs = CrawlToBundleConverter.BuildContainerDefs(themeName);
                templateDefs = CrawlToBundleConverter.BuildTemplateDef(
                    layout, containerDefs[0].id);
                Console.WriteLine(
                    $"Layout extracted from first page — theme '{themeName}' with " +
                    $"{containerDefs.Count} container(s) and {templateDefs.Count} template(s).");
            }
        }

        if (File.Exists(outputPath))
            File.Delete(outputPath);

        using (var outStream = File.Create(outputPath))
            BundleWriter.Write(contentTypes, outStream, themesZipPath: null, siteName,
                htmlContents, portalPages, portalFiles, containerDefs, templateDefs);

        Console.WriteLine(
            $"Bundle written to: {outputPath}" +
            $" ({htmlContents.Count} content item(s)," +
            $" {portalPages.Count} page(s)," +
            $" {portalFiles.Count} static file(s)).");
        return 0;
    }
    catch (HttpRequestException ex)
    {
        Console.Error.WriteLine($"HTTP error while crawling: {ex.Message}");
        return 1;
    }
    catch (TaskCanceledException)
    {
        Console.Error.WriteLine("Crawl timed out.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unexpected error during crawl: {ex.Message}");
        return 1;
    }
}

// ---------------------------------------------------------------------------

static void PrintUsage()
{
    Console.WriteLine("""
        DNN to DotCMS Converter v1.0.0
        Converts DNN (DotNetNuke) site exports — or a live website — to a
        DotCMS push-publish bundle.

        Usage:
          DnnToDotCms <input> [--output <site.tar.gz>]
          DnnToDotCms --crawl <url> [--max-pages <n>] [--output <site.tar.gz>]
          DnnToDotCms --help

        Arguments:
          <input>             Path to a DNN official site-export folder, or the
                              export.json manifest inside that folder, or a DNN
                              XML file (.dnn or IPortable export)

        Options:
          --crawl <url>       Crawl a live website and create a bundle from its
                              HTML pages and static assets (images, CSS, JS)
          --max-pages <n>     Maximum number of pages to crawl (default: 200,
                              only used with --crawl)
          --output <path>     Write the bundle to a specific file
                              (default: site.tar.gz in the current directory)
          --help, -h          Show this help message

        Supported DNN module types (export-folder mode):
          HTML / Text-HTML, Announcements, Events, FAQs, Forms, Blog,
          Documents, Links, Contacts, News Feed, Gallery, Feedback.
          Unknown module types produce a generic HTMLContent content type.

        Output:
          A DotCMS push-publish site bundle (.tar.gz) containing:
            • working/System Host/{uuid}.contentType.json          — one file per content type
            • working/System Host/{uuid}.containers.container.xml  — one file per DNN container
            • working/System Host/{uuid}.template.template.xml     — one file per DNN skin
            • manifest.csv                                         — bundle manifest
            • ROOT/application/themes/{ThemeName}/…                — static theme assets
              (CSS, JS, images and fonts from export_themes.zip)

        Examples:
          DnnToDotCms example/2026-03-29_01-49-26
          DnnToDotCms example/2026-03-29_01-49-26/export.json
          DnnToDotCms example/2026-03-29_01-49-26 --output my-site.tar.gz
          DnnToDotCms site-export.dnn
          DnnToDotCms module-export.xml
          DnnToDotCms --crawl https://www.example.com
          DnnToDotCms --crawl https://www.example.com --max-pages 50 --output crawled.tar.gz
        """);
}

