using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DnnToDotCms.Crawler;
using DnnToDotCms.Models;
using LiteDB;

namespace DnnToDotCms.Parser;

/// <summary>
/// Parses DNN (DotNetNuke) XML exports into <see cref="DnnModule"/> objects.
/// <para>
/// Three formats are supported:
/// <list type="bullet">
///   <item><description>
///     <b>Official site export folder</b> — the folder produced by DNN's built-in
///     Export/Import feature (e.g. <c>2026-03-29_01-49-26/</c>). The folder must
///     contain an <c>export_packages.zip</c> file, which holds one or more
///     <c>Module_*.resources</c> archives. Each <c>.resources</c> file is itself a
///     ZIP that contains a <c>.dnn</c> package manifest.
///   </description></item>
///   <item><description>
///     <b>Package manifest</b> — the standard <c>.dnn</c> installation manifest
///     with a <c>&lt;dotnetnuke type="Package"&gt;</c> root element.
///   </description></item>
///   <item><description>
///     <b>IPortable module-content export</b> — produced when a module that
///     implements <c>IPortable</c> is exported; root element is
///     <c>&lt;module type="…"&gt;</c>.
///   </description></item>
/// </list>
/// </para>
/// </summary>
public static class DnnXmlParser
{
    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads the portal / site name from a DNN export folder or <c>export.json</c>
    /// manifest file.  Returns <see langword="null"/> when the file does not
    /// exist or cannot be parsed.
    /// </summary>
    /// <param name="exportFolderOrJson">
    /// Either the DNN export folder path or the full path to <c>export.json</c>.
    /// </param>
    public static string? ParsePortalName(string exportFolderOrJson)
    {
        string jsonPath = Directory.Exists(exportFolderOrJson)
            ? Path.Combine(exportFolderOrJson, "export.json")
            : exportFolderOrJson;

        if (!File.Exists(jsonPath))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            return doc.RootElement.TryGetProperty("PortalName", out JsonElement pn)
                ? pn.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts HTML module content from a DNN official site-export folder by
    /// reading the LiteDB database in <c>export_db.zip</c>.
    /// Returns an empty list if the file does not exist, cannot be opened, or
    /// contains no HTML module content.
    /// </summary>
    /// <param name="exportFolderOrJson">
    /// Either the DNN export folder path or the full path to <c>export.json</c>
    /// inside that folder.
    /// </param>
    public static IReadOnlyList<DnnHtmlContent> ParseHtmlContents(string exportFolderOrJson)
        => WithLiteDb(exportFolderOrJson, db => ExtractHtmlContents(db));

    /// <summary>
    /// Overload that accepts pre-scraped slider data from a live site.
    /// When <paramref name="scrapedSlides"/> is provided, FisSlider modules
    /// use the scraped image URLs, link destinations, and captions instead of
    /// placeholders.
    /// </summary>
    public static IReadOnlyList<DnnHtmlContent> ParseHtmlContents(
        string exportFolderOrJson,
        IReadOnlyList<ScrapedSlide>? scrapedSlides)
        => WithLiteDb(exportFolderOrJson, db => ExtractHtmlContents(db, scrapedSlides));

    // ------------------------------------------------------------------
    // Shared LiteDB helper
    // ------------------------------------------------------------------

    private static string ResolveFolderPath(string exportFolderOrJson)
    {
        if (Directory.Exists(exportFolderOrJson))
            return exportFolderOrJson;

        string? parent = Path.GetDirectoryName(Path.GetFullPath(exportFolderOrJson));
        return parent ?? exportFolderOrJson;
    }

    private static IReadOnlyList<T> WithLiteDb<T>(
        string exportFolderOrJson,
        Func<LiteDatabase, IReadOnlyList<T>> action)
    {
        string folderPath  = ResolveFolderPath(exportFolderOrJson);
        string dbZipPath   = Path.Combine(folderPath, "export_db.zip");
        if (!File.Exists(dbZipPath))
            return [];

        string tempDb = Path.GetTempFileName();
        try
        {
            using (var zipStream = File.OpenRead(dbZipPath))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                ZipArchiveEntry? dbEntry = zip.GetEntry("export.dnndb");
                if (dbEntry is null)
                    return [];

                using var outStream = File.Create(tempDb);
                using var inStream  = dbEntry.Open();
                inStream.CopyTo(outStream);
            }

            using var db = new LiteDatabase($"Filename={tempDb};ReadOnly=true");
            return action(db);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                      or LiteException or InvalidOperationException)
        {
            Console.Error.WriteLine(
                $"Warning: Could not read data from '{dbZipPath}': {ex.Message}");
            return [];
        }
        finally
        {
            try { File.Delete(tempDb); } catch (IOException) { /* best-effort cleanup */ }
        }
    }

    // ------------------------------------------------------------------
    // Public API — portal pages
    // ------------------------------------------------------------------

    /// <summary>
    /// Extracts all DNN portal pages (tabs) from the <c>ExportTab</c>
    /// collection in <c>export_db.zip</c>.
    /// Returns an empty list when the database is unavailable or contains
    /// no tab records.
    /// </summary>
    /// <param name="exportFolderOrJson">
    /// Either the DNN export folder path or the full path to <c>export.json</c>.
    /// </param>
    public static IReadOnlyList<DnnPortalPage> ParsePortalPages(string exportFolderOrJson)
        => WithLiteDb(exportFolderOrJson, ExtractPortalPages);

    // ------------------------------------------------------------------
    // Public API — portal files
    // ------------------------------------------------------------------

    /// <summary>
    /// Extracts portal static files from <c>export_db.zip</c> (metadata) and
    /// <c>export_files.zip</c> (binary content).
    /// Returns an empty list when either archive is missing or unreadable.
    /// </summary>
    /// <param name="exportFolderOrJson">
    /// Either the DNN export folder path or the full path to <c>export.json</c>.
    /// </param>
    public static IReadOnlyList<DnnPortalFile> ParsePortalFiles(string exportFolderOrJson)
    {
        string folderPath = ResolveFolderPath(exportFolderOrJson);

        string filesZip = Path.Combine(folderPath, "export_files.zip");
        if (!File.Exists(filesZip))
            return [];

        // Read actual file bytes from export_files.zip into a lookup.
        var fileBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = ZipFile.OpenRead(filesZip);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                using var ms   = new MemoryStream();
                using var inSt = entry.Open();
                inSt.CopyTo(ms);
                // Normalise separators so lookups work on both platforms.
                fileBytes[entry.FullName.Replace('\\', '/')] = ms.ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.Error.WriteLine(
                $"Warning: Could not read portal files from '{filesZip}': {ex.Message}");
            return [];
        }

        return WithLiteDb(exportFolderOrJson, db => ExtractPortalFiles(db, fileBytes));
    }

    // ------------------------------------------------------------------
    // LiteDB HTML content extraction
    // ------------------------------------------------------------------

    private static IReadOnlyList<DnnHtmlContent> ExtractHtmlContents(
        LiteDatabase db, IReadOnlyList<ScrapedSlide>? scrapedSlides = null)
    {
        // Build a mapping of TabID (int) → UniqueId (GUID string) from ExportTab.
        var tabUniqueIds = new Dictionary<int, string>();
        ILiteCollection<BsonDocument> tabs = db.GetCollection("ExportTab");
        foreach (BsonDocument doc in tabs.FindAll())
        {
            if (!doc.TryGetValue("TabID",    out BsonValue tabIdVal))  continue;
            if (!doc.TryGetValue("UniqueId", out BsonValue uidVal))    continue;
            tabUniqueIds[tabIdVal.AsInt32] = uidVal.AsGuid.ToString();
        }

        // Build mappings of ModuleID → display title and ModuleID → list of
        // (tab UniqueId, PaneName) from ExportTabModule.  A module may appear
        // on many tabs (e.g. shared footer modules), so we collect ALL
        // associations together with the pane each module instance lives in.
        var moduleTitles    = new Dictionary<int, string>();
        var moduleTabPanes  = new Dictionary<int, List<(string tabUniqueId, string paneName, string containerSrc, string iconFile)>>();
        var moduleSeenTabs  = new Dictionary<int, HashSet<string>>(); // dedup guard

        // Build a mapping of FileId → folder/fileName from ExportFile so that
        // IconFile values like "FileID=58791" can be resolved to actual paths.
        // Also collect image files grouped by folder so FisSlider modules can
        // have a carousel generated from any slider-named image folders.
        var fileIdPaths = new Dictionary<int, string>();
        var imageFilesByFolder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        ILiteCollection<BsonDocument> exportFiles = db.GetCollection("ExportFile");
        foreach (BsonDocument doc in exportFiles.FindAll())
        {
            if (!doc.TryGetValue("FileId", out BsonValue fIdVal)) continue;
            string folder = doc.TryGetValue("Folder", out BsonValue folderVal)
                ? (folderVal.AsString ?? string.Empty) : string.Empty;
            string fileName = doc.TryGetValue("FileName", out BsonValue fnVal)
                ? (fnVal.AsString ?? string.Empty) : string.Empty;
            if (!string.IsNullOrEmpty(fileName))
                fileIdPaths[fIdVal.AsInt32] = folder + fileName;

            // Collect image files for slider carousel generation.
            string contentType = doc.TryGetValue("ContentType", out BsonValue ctVal)
                ? (ctVal.AsString ?? string.Empty) : string.Empty;
            if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(fileName))
            {
                if (!imageFilesByFolder.TryGetValue(folder, out var imgs))
                {
                    imgs = [];
                    imageFilesByFolder[folder] = imgs;
                }
                imgs.Add(folder + fileName);
            }
        }

        ILiteCollection<BsonDocument> tabModules = db.GetCollection("ExportTabModule");
        foreach (BsonDocument doc in tabModules.FindAll())
        {
            if (!doc.TryGetValue("ModuleID", out BsonValue moduleIdVal))
                continue;

            int moduleId = moduleIdVal.AsInt32;

            if (doc.TryGetValue("ModuleTitle", out BsonValue titleVal))
            {
                string title = titleVal.AsString ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(title))
                    moduleTitles.TryAdd(moduleId, title);
            }

            if (doc.TryGetValue("TabID", out BsonValue tabIdVal)
                && tabUniqueIds.TryGetValue(tabIdVal.AsInt32, out string? tabUniqueId))
            {
                string paneName = doc.TryGetValue("PaneName", out BsonValue paneVal)
                    ? (paneVal.AsString ?? string.Empty)
                    : string.Empty;

                string containerSrc = doc.TryGetValue("ContainerSrc", out BsonValue cVal)
                    ? (cVal.AsString ?? string.Empty)
                    : string.Empty;

                // Resolve IconFile: may be a path like "Images/foo.png" or
                // a reference like "FileID=58791".
                string iconFile = doc.TryGetValue("IconFile", out BsonValue iVal)
                    ? (iVal.AsString ?? string.Empty)
                    : string.Empty;
                if (iconFile.StartsWith("FileID=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(iconFile.AsSpan("FileID=".Length), out int fid)
                    && fileIdPaths.TryGetValue(fid, out string? resolvedPath))
                {
                    iconFile = resolvedPath;
                }

                if (!moduleTabPanes.TryGetValue(moduleId, out var tabPanes))
                {
                    tabPanes = [];
                    moduleTabPanes[moduleId] = tabPanes;
                    moduleSeenTabs[moduleId] = [];
                }
                // Avoid duplicate tab associations for the same module.
                if (!moduleSeenTabs.TryGetValue(moduleId, out var seen))
                {
                    seen = [];
                    moduleSeenTabs[moduleId] = seen;
                }
                if (seen.Add(tabUniqueId))
                    tabPanes.Add((tabUniqueId, paneName, containerSrc, iconFile));
            }
        }

        var results = new List<DnnHtmlContent>();
        ILiteCollection<BsonDocument> moduleContents = db.GetCollection("ExportModuleContent");

        // Track which modules have content in ExportModuleContent so we can
        // create placeholder entries for modules that don't (e.g. custom
        // modules like FisSlider that store data in their own SQL tables).
        var modulesWithContent = new HashSet<int>();

        foreach (BsonDocument doc in moduleContents.FindAll())
        {
            if (!doc.TryGetValue("ModuleID",    out BsonValue moduleIdVal))  continue;
            if (!doc.TryGetValue("XmlContent",  out BsonValue xmlContentVal)) continue;

            string xmlContent = xmlContentVal.AsString ?? string.Empty;
            if (string.IsNullOrWhiteSpace(xmlContent))
                continue;

            string? htmlBody = ExtractHtmlBodyFromDnnXml(xmlContent);
            if (htmlBody is null)
                continue;

            int moduleId = moduleIdVal.AsInt32;
            modulesWithContent.Add(moduleId);
            moduleTitles.TryGetValue(moduleId,    out string? title);
            string displayTitle = string.IsNullOrWhiteSpace(title) ? "Content" : title;

            // Create a content entry for EVERY tab the module appears on.
            // Shared modules (e.g. footer) appear on many tabs in DNN and
            // their content must be present on each corresponding DotCMS page.
            if (moduleTabPanes.TryGetValue(moduleId, out var associatedTabPanes) && associatedTabPanes.Count > 0)
            {
                foreach (var (tabUniqueId, paneName, containerSrc, iconFile) in associatedTabPanes)
                {
                    results.Add(new DnnHtmlContent(
                        Title:       displayTitle,
                        HtmlBody:    htmlBody,
                        TabUniqueId: tabUniqueId,
                        PaneName:    paneName,
                        ContainerSrc: containerSrc,
                        IconFile:    iconFile));
                }
            }
            else
            {
                // Module has no tab association; include it anyway so it
                // appears in the bundle even if unlinked to a page.
                results.Add(new DnnHtmlContent(
                    Title:       displayTitle,
                    HtmlBody:    htmlBody,
                    TabUniqueId: string.Empty));
            }
        }

        // Build a mapping of ModuleID → FriendlyName from ExportModule so
        // placeholder entries for non-HTML modules include the module type.
        var moduleFriendlyNames = new Dictionary<int, string>();
        ILiteCollection<BsonDocument> exportModules = db.GetCollection("ExportModule");
        foreach (BsonDocument doc in exportModules.FindAll())
        {
            if (!doc.TryGetValue("ModuleID", out BsonValue midVal)) continue;
            string fn = doc.TryGetValue("FriendlyName", out BsonValue fnVal)
                ? (fnVal.AsString ?? string.Empty) : string.Empty;
            if (!string.IsNullOrWhiteSpace(fn))
                moduleFriendlyNames[midVal.AsInt32] = fn;
        }

        // Create entries for modules that have page/pane associations but no
        // content in ExportModuleContent (e.g. custom modules like FisSlider
        // that store data in their own SQL tables).  FisSlider modules receive
        // a Bootstrap 5 carousel populated with any slider images found in the
        // portal file export; other custom modules get a generic placeholder.
        foreach (var (moduleId, tabPanes) in moduleTabPanes)
        {
            if (modulesWithContent.Contains(moduleId))
                continue;

            moduleTitles.TryGetValue(moduleId, out string? title);
            string displayTitle = string.IsNullOrWhiteSpace(title) ? "Content" : title;

            moduleFriendlyNames.TryGetValue(moduleId, out string? friendlyName);
            string moduleType = string.IsNullOrWhiteSpace(friendlyName) ? "Custom Module" : friendlyName;

            string body;
            if (string.Equals(moduleType, "FisSlider", StringComparison.OrdinalIgnoreCase))
            {
                // Collect images from portal folders whose name contains "slider"
                // (case-insensitive), e.g. "FisSlider-Images/".  Sort them so
                // the carousel order is deterministic across runs.
                List<string> sliderImages = imageFilesByFolder
                    .Where(kv => kv.Key.Contains("slider", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(kv => kv.Value)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                body = BuildSliderHtml(displayTitle, sliderImages, scrapedSlides);
            }
            else
            {
                // Generic placeholder for other custom modules so that content
                // editors know what needs to be recreated in DotCMS.
                string escapedType  = System.Security.SecurityElement.Escape(moduleType) ?? moduleType;
                string escapedTitle = System.Security.SecurityElement.Escape(displayTitle) ?? displayTitle;
                body =
                    $"""
                    <div class="dnn-module-placeholder" data-module-type="{escapedType}">
                      <p><strong>{escapedType}</strong>: {escapedTitle}</p>
                      <p><em>This content was managed by a custom DNN module and needs to be recreated in DotCMS.</em></p>
                    </div>
                    """;
            }

            foreach (var (tabUniqueId, paneName, containerSrc, iconFile) in tabPanes)
            {
                results.Add(new DnnHtmlContent(
                    Title:        displayTitle,
                    HtmlBody:     body,
                    TabUniqueId:  tabUniqueId,
                    PaneName:     paneName,
                    ContainerSrc: containerSrc,
                    IconFile:     iconFile));
            }
        }

        return results;
    }

    // ------------------------------------------------------------------
    // LiteDB portal pages extraction
    // ------------------------------------------------------------------

    private static IReadOnlyList<DnnPortalPage> ExtractPortalPages(LiteDatabase db)
    {
        var results = new List<DnnPortalPage>();
        ILiteCollection<BsonDocument> tabs = db.GetCollection("ExportTab");

        foreach (BsonDocument doc in tabs.FindAll())
        {
            if (!doc.TryGetValue("TabName",  out BsonValue nameVal))   continue;
            if (!doc.TryGetValue("UniqueId", out BsonValue uidVal))    continue;
            if (!doc.TryGetValue("IsDeleted",out BsonValue deletedVal)) continue;
            if (deletedVal.AsBoolean) continue;

            doc.TryGetValue("Title",       out BsonValue titleVal);
            doc.TryGetValue("Description", out BsonValue descVal);
            doc.TryGetValue("TabPath",     out BsonValue pathVal);
            doc.TryGetValue("Level",       out BsonValue levelVal);
            doc.TryGetValue("IsVisible",   out BsonValue visibleVal);
            doc.TryGetValue("SkinSrc",     out BsonValue skinVal);

            string uniqueId = uidVal.AsGuid.ToString();
            string name     = nameVal.AsString  ?? string.Empty;
            string title    = titleVal?.AsString ?? name;
            string desc     = descVal?.AsString  ?? string.Empty;
            string tabPath  = pathVal?.AsString  ?? string.Empty;
            int level       = levelVal?.AsInt32  ?? 0;
            bool isVisible  = visibleVal?.AsBoolean ?? true;
            string skinSrc  = skinVal?.AsString  ?? string.Empty;

            results.Add(new DnnPortalPage(uniqueId, name, title, desc,
                tabPath, level, isVisible, skinSrc));
        }

        return results;
    }

    // ------------------------------------------------------------------
    // LiteDB portal files extraction
    // ------------------------------------------------------------------

    private static IReadOnlyList<DnnPortalFile> ExtractPortalFiles(
        LiteDatabase db,
        Dictionary<string, byte[]> fileBytes)
    {
        var results = new List<DnnPortalFile>();
        ILiteCollection<BsonDocument> files = db.GetCollection("ExportFile");

        foreach (BsonDocument doc in files.FindAll())
        {
            if (!doc.TryGetValue("FileName",    out BsonValue fileNameVal)) continue;
            if (!doc.TryGetValue("UniqueId",    out BsonValue uidVal))      continue;
            if (!doc.TryGetValue("VersionGuid", out BsonValue versionVal))  continue;

            doc.TryGetValue("Folder",      out BsonValue folderVal);
            doc.TryGetValue("ContentType", out BsonValue ctVal);

            string fileName    = fileNameVal.AsString ?? string.Empty;
            if (string.IsNullOrWhiteSpace(fileName)) continue;

            string folderPath  = folderVal?.AsString ?? string.Empty;
            // Normalise folder path: DNN may omit the trailing slash in some export
            // versions (e.g. "Images" instead of "Images/").  Always ensure a
            // trailing slash on non-empty paths so downstream code works uniformly.
            if (folderPath.Length > 0 && !folderPath.EndsWith('/'))
                folderPath += '/';
            string mimeType    = ctVal?.AsString     ?? "application/octet-stream";
            string uniqueId    = uidVal.AsGuid.ToString();
            string versionGuid = versionVal.AsGuid.ToString();

            // Locate the actual file bytes in the zip using the DNN relative path.
            // Ensure proper path joining and normalise separators (DNN may use either).
            string zipKey = Path.Combine(folderPath, fileName).Replace('\\', '/');
            if (!fileBytes.TryGetValue(zipKey, out byte[]? content))
                continue;  // file missing from export_files.zip – skip

            results.Add(new DnnPortalFile(uniqueId, versionGuid,
                fileName, folderPath, mimeType, content));
        }

        return results;
    }


    /// <summary>
    /// Builds a Bootstrap 5 carousel (<c>div.carousel</c>) for a FisSlider
    /// module instance.  When <paramref name="scrapedSlides"/> is provided
    /// (from a live site scrape via <c>--site-url</c>), the carousel uses
    /// scraped image URLs, link destinations, and captions.  Otherwise
    /// falls back to export-only data: image filenames as captions and
    /// placeholder links.
    /// </summary>
    /// <param name="title">
    /// The DNN module title — used to derive a stable, unique HTML element ID
    /// for the carousel so that prev/next controls bind to the correct widget.
    /// </param>
    /// <param name="imagePaths">
    /// Zero or more portal-relative image paths found in slider-named folders
    /// (e.g. <c>FisSlider-Images/slide.jpg</c>).  Each path is prefixed with
    /// <c>/</c> so it resolves from the DotCMS site root.
    /// </param>
    /// <param name="scrapedSlides">
    /// Optional slides scraped from the live site.  When provided and
    /// non-empty, these take priority over <paramref name="imagePaths"/>
    /// because they include link URLs, captions, and descriptions that
    /// are stored in the <c>FisSlider_Slides</c> SQL table and not
    /// available in DNN exports.
    /// </param>
    internal static string BuildSliderHtml(
        string title,
        IReadOnlyList<string> imagePaths,
        IReadOnlyList<ScrapedSlide>? scrapedSlides = null)
    {
        // Derive a safe HTML id attribute value from the module title.
        // Include a short SHA-256 hash of the original title to prevent
        // collisions between sliders whose titles share all alphanumeric chars
        // (e.g. "Banner #1" and "Banner $1" would both normalise to "banner1").
        string safeName = Regex.Replace(title, @"[^a-zA-Z0-9]", string.Empty).ToLowerInvariant();
        if (string.IsNullOrEmpty(safeName)) safeName = "slider";
        byte[] hashBytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(title));
        string shortHash = Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
        string carouselId = $"dnn-slider-{safeName}-{shortHash}";

        // Use scraped slides when available; otherwise fall back to
        // export-only image paths with placeholder metadata.
        bool useScraped = scrapedSlides is not null && scrapedSlides.Count > 0;
        int slideCount = useScraped ? scrapedSlides!.Count : imagePaths.Count;

        var sb = new StringBuilder();
        sb.AppendLine($"""<div id="{carouselId}" class="carousel slide" data-bs-ride="carousel">""");

        if (slideCount > 0)
        {
            // Carousel indicators (navigation dots).
            sb.AppendLine("""  <div class="carousel-indicators">""");
            for (int i = 0; i < slideCount; i++)
            {
                string extraAttrs = i == 0 ? " class=\"active\" aria-current=\"true\"" : string.Empty;
                sb.AppendLine($"""    <button type="button" data-bs-target="#{carouselId}" data-bs-slide-to="{i}"{extraAttrs} aria-label="Slide {i + 1}"></button>""");
            }
            sb.AppendLine("  </div>");

            // Carousel slides.
            sb.AppendLine("""  <div class="carousel-inner">""");
            for (int i = 0; i < slideCount; i++)
            {
                string activeClass = i == 0 ? " active" : string.Empty;
                string src;
                string altText;
                string linkHref;
                string? captionHeading;
                string? captionDescription;

                if (useScraped)
                {
                    var slide = scrapedSlides![i];
                    src = slide.ImageUrl;
                    altText = !string.IsNullOrWhiteSpace(slide.Caption)
                        ? System.Security.SecurityElement.Escape(slide.Caption) ?? $"Slide {i + 1}"
                        : $"Slide {i + 1}";
                    linkHref = slide.LinkUrl ?? "#";
                    captionHeading = slide.Caption;
                    captionDescription = slide.Description;
                }
                else
                {
                    src = "/" + imagePaths[i].TrimStart('/');
                    altText = Path.GetFileNameWithoutExtension(imagePaths[i]);
                    if (string.IsNullOrWhiteSpace(altText)) altText = $"Slide {i + 1}";
                    linkHref = "#";
                    captionHeading = altText;
                    captionDescription = null;
                }

                sb.AppendLine($"""    <div class="carousel-item{activeClass}">""");
                sb.AppendLine($"""      <img src="{src}" class="d-block w-100" alt="{altText}">""");
                sb.AppendLine("""      <div class="carousel-caption d-none d-md-block">""");
                if (!string.IsNullOrWhiteSpace(captionHeading))
                {
                    string escapedCaption = System.Security.SecurityElement.Escape(captionHeading) ?? captionHeading;
                    sb.AppendLine($"""        <h5>{escapedCaption}</h5>""");
                }
                if (!string.IsNullOrWhiteSpace(captionDescription))
                {
                    string escapedDesc = System.Security.SecurityElement.Escape(captionDescription) ?? captionDescription;
                    sb.AppendLine($"""        <p>{escapedDesc}</p>""");
                }
                if (!useScraped || linkHref == "#")
                {
                    sb.AppendLine("""        <!-- TODO: replace '#' with the slide destination URL -->""");
                }
                sb.AppendLine($"""        <a href="{linkHref}" class="btn btn-primary">Learn More</a>""");
                sb.AppendLine("      </div>");
                sb.AppendLine("    </div>");
            }
            sb.AppendLine("  </div>");
        }
        else
        {
            // No images found in the export — produce an empty carousel shell
            // that content editors can populate directly inside DotCMS.
            sb.AppendLine("""  <div class="carousel-inner">""");
            sb.AppendLine("""    <div class="carousel-item active">""");
            sb.AppendLine("    </div>");
            sb.AppendLine("  </div>");
        }

        // Previous / next controls.
        sb.AppendLine($"""  <button class="carousel-control-prev" type="button" data-bs-target="#{carouselId}" data-bs-slide="prev">""");
        sb.AppendLine("""    <span class="carousel-control-prev-icon" aria-hidden="true"></span>""");
        sb.AppendLine("""    <span class="visually-hidden">Previous</span>""");
        sb.AppendLine("  </button>");
        sb.AppendLine($"""  <button class="carousel-control-next" type="button" data-bs-target="#{carouselId}" data-bs-slide="next">""");
        sb.AppendLine("""    <span class="carousel-control-next-icon" aria-hidden="true"></span>""");
        sb.AppendLine("""    <span class="visually-hidden">Next</span>""");
        sb.AppendLine("  </button>");
        sb.Append("</div>");

        return sb.ToString();
    }

    /// <summary>
    /// Extracts and HTML-decodes the content stored inside a DNN HTML module's
    /// <c>&lt;htmltext&gt;&lt;content&gt;&lt;![CDATA[…]]&gt;&lt;/content&gt;&lt;/htmltext&gt;</c>
    /// XML payload.  Returns <see langword="null"/> when the input cannot be
    /// parsed or contains no recognisable content.
    /// </summary>
    public static string? ExtractHtmlBodyFromDnnXml(string xmlContent)
    {
        try
        {
            XDocument doc     = XDocument.Parse(xmlContent);
            XElement? content = doc.Root?.Element("content");
            if (content is null)
                return null;

            // The CDATA value still has HTML entities encoded (e.g. &lt;, &quot;).
            // Decode them to get the actual HTML markup.
            string decoded = System.Net.WebUtility.HtmlDecode(content.Value);
            if (string.IsNullOrWhiteSpace(decoded))
                return null;

            // Replace the DNN portal-root token {{PortalRoot}} with "/".
            // In DNN, {{PortalRoot}} expands to "/Portals/{id}/" at runtime.
            // After migration, portal files are imported as DotCMS FileAsset
            // contentlets whose folder path mirrors the original DNN structure
            // (e.g. /Images/, /Documents/), so the token simply becomes "/".
            // e.g. "{{PortalRoot}}Images/logo.png" → "/Images/logo.png"
            //      "{{PortalRoot}}home.css"         → "/home.css"
            decoded = decoded.Replace("{{PortalRoot}}", "/", StringComparison.OrdinalIgnoreCase);

            return decoded.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse a DNN official site-export from an <c>export.json</c> manifest file.
    /// Locates <c>export_packages.zip</c> in the same directory as the manifest
    /// and delegates to <see cref="ParseExportFolder"/>.
    /// </summary>
    /// <param name="jsonFilePath">
    /// Path to the <c>export.json</c> file inside a DNN site-export folder.
    /// </param>
    /// <exception cref="FileNotFoundException">
    /// Thrown when <c>export_packages.zip</c> is not found alongside the manifest.
    /// </exception>
    public static IReadOnlyList<DnnModule> ParseExportJson(string jsonFilePath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(jsonFilePath))
            ?? throw new ArgumentException(
                $"Cannot determine directory for: {jsonFilePath}", nameof(jsonFilePath));

        return ParseExportFolder(folder);
    }

    /// <summary>
    /// Parse a DNN official site-export folder and return a de-duplicated list
    /// of <see cref="DnnModule"/> objects discovered from all
    /// <c>Module_*.resources</c> entries inside <c>export_packages.zip</c>.
    /// </summary>
    /// <param name="folderPath">
    /// Path to the export folder that contains <c>export_packages.zip</c>
    /// (e.g. the <c>2026-03-29_01-49-26</c> folder created by DNN Export).
    /// </param>
    /// <exception cref="FileNotFoundException">
    /// Thrown when <c>export_packages.zip</c> is not found inside
    /// <paramref name="folderPath"/>.
    /// </exception>
    public static IReadOnlyList<DnnModule> ParseExportFolder(string folderPath)
    {
        string packagesZip = Path.Combine(folderPath, "export_packages.zip");
        if (!File.Exists(packagesZip))
            throw new FileNotFoundException(
                $"export_packages.zip not found in folder: {folderPath}", packagesZip);

        var modules = new List<DnnModule>();

        using ZipArchive outer = ZipFile.OpenRead(packagesZip);
        foreach (ZipArchiveEntry resourceEntry in outer.Entries)
        {
            // Only process Module_*.resources entries
            if (!resourceEntry.Name.StartsWith("Module_", StringComparison.OrdinalIgnoreCase) ||
                !resourceEntry.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                continue;

            using Stream resourceStream = resourceEntry.Open();
            using var resourceZip = new ZipArchive(resourceStream, ZipArchiveMode.Read);

            foreach (ZipArchiveEntry dnnEntry in resourceZip.Entries)
            {
                if (!dnnEntry.Name.EndsWith(".dnn", StringComparison.OrdinalIgnoreCase))
                    continue;

                using StreamReader reader = new(dnnEntry.Open());
                string xmlContent = reader.ReadToEnd();
                modules.AddRange(ParseXml(xmlContent));
            }
        }

        return modules;
    }

    /// <summary>
    /// Parse a DNN XML string and return a list of <see cref="DnnModule"/>.
    /// </summary>
    /// <param name="xmlContent">Raw XML text.</param>
    /// <returns>One or more parsed modules.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="xmlContent"/> is empty.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the XML has an unrecognised root element.
    /// </exception>
    public static IReadOnlyList<DnnModule> ParseXml(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
            throw new ArgumentException("XML content must not be empty.", nameof(xmlContent));

        XDocument doc = XDocument.Parse(xmlContent);
        XElement root = doc.Root
            ?? throw new InvalidOperationException("XML document has no root element.");

        string rootTag = root.Name.LocalName.ToLowerInvariant();

        return rootTag switch
        {
            "dotnetnuke" => ParsePackageManifest(root),
            "module"     => [ParseIPortableModule(root)],
            "desktopmodule" or "components" => ParseDesktopModuleElement(root),
            _ => throw new InvalidOperationException(
                $"Unrecognised DNN XML root element: <{root.Name.LocalName}>. " +
                "Expected <dotnetnuke>, <module>, or <desktopModule>.")
        };
    }

    /// <summary>
    /// Read a file at <paramref name="path"/> and delegate to <see cref="ParseXml"/>.
    /// </summary>
    public static IReadOnlyList<DnnModule> ParseFile(string path) =>
        ParseXml(File.ReadAllText(path));

    // ------------------------------------------------------------------
    // Format: <dotnetnuke type="Package" …>
    // ------------------------------------------------------------------

    private static IReadOnlyList<DnnModule> ParsePackageManifest(XElement root)
    {
        var modules = new List<DnnModule>();

        foreach (XElement pkg in root.Descendants("package"))
        {
            string pkgType = (string?)pkg.Attribute("type") ?? string.Empty;
            if (!pkgType.Equals("Module", StringComparison.OrdinalIgnoreCase))
                continue;

            string pkgName    = (string?)pkg.Attribute("name")    ?? string.Empty;
            string pkgVersion = (string?)pkg.Attribute("version") ?? "1.0.0";
            string friendly   = Text(pkg, "friendlyName") ?? pkgName;
            string desc       = Text(pkg, "description")  ?? string.Empty;

            XElement? dm = pkg.Descendants("desktopModule").FirstOrDefault();
            if (dm is null)
            {
                modules.Add(new DnnModule(pkgName, friendly, desc, Version: pkgVersion));
                continue;
            }

            modules.Add(BuildModuleFromDesktopElement(dm, pkgName, friendly, desc, pkgVersion));
        }

        return modules;
    }

    // ------------------------------------------------------------------
    // Format: bare <desktopModule> / <components>
    // ------------------------------------------------------------------

    private static IReadOnlyList<DnnModule> ParseDesktopModuleElement(XElement root)
    {
        if (root.Name.LocalName.Equals("components", StringComparison.OrdinalIgnoreCase))
        {
            var results = new List<DnnModule>();
            foreach (XElement comp in root.Descendants("component")
                         .Where(c => ((string?)c.Attribute("type") ?? "")
                             .Equals("Module", StringComparison.OrdinalIgnoreCase)))
            {
                XElement? dm = comp.Element("desktopModule");
                if (dm is not null)
                    results.Add(BuildModuleFromDesktopElement(dm));
            }
            return results;
        }

        // root is already <desktopModule>
        return [BuildModuleFromDesktopElement(root)];
    }

    private static DnnModule BuildModuleFromDesktopElement(
        XElement dm,
        string fallbackName        = "",
        string fallbackFriendly    = "",
        string fallbackDescription = "",
        string version             = "1.0.0")
    {
        string moduleName  = Text(dm, "moduleName")  ?? fallbackName;
        string friendly    = Text(dm, "friendlyName") ?? fallbackFriendly ?? moduleName;
        string description = Text(dm, "description") ?? fallbackDescription;
        string folder      = Text(dm, "foldername")  ?? Text(dm, "folderName") ?? string.Empty;
        string bcc         = Text(dm, "businessControllerClass") ?? string.Empty;

        var definitions = dm.Descendants("moduleDefinition")
            .Select(ParseModuleDefinition)
            .ToList();

        return new DnnModule(moduleName, friendly, description, folder, bcc, version)
        {
            Definitions = definitions
        };
    }

    // ------------------------------------------------------------------
    // Format: <module type="…"> (IPortable export)
    // ------------------------------------------------------------------

    private static DnnModule ParseIPortableModule(XElement root)
    {
        string moduleType = (string?)root.Attribute("type")    ?? "Unknown";
        string version    = (string?)root.Attribute("version") ?? "1.0.0";
        string title      = Text(root, "moduleTitle") ?? moduleType;

        XElement? contentEl = root.Element("moduleContent");
        string? content = null;
        if (contentEl is not null)
        {
            content = contentEl.Value.Trim();
            if (string.IsNullOrEmpty(content) && contentEl.HasElements)
                content = contentEl.ToString();
        }

        var extra = new Dictionary<string, string>();
        foreach (XElement child in root.Elements()
                     .Where(e => e.Name.LocalName is not ("moduleTitle" or "moduleContent")))
        {
            extra[child.Name.LocalName] = child.Value.Trim();
        }

        return new DnnModule(moduleType, title, Version: version)
        {
            Content = content,
            Extra   = extra
        };
    }

    // ------------------------------------------------------------------
    // Module definition / control helpers
    // ------------------------------------------------------------------

    private static DnnModuleDefinition ParseModuleDefinition(XElement def)
    {
        string friendly = Text(def, "friendlyName") ?? string.Empty;
        int cacheTime   = int.TryParse(Text(def, "defaultCacheTime"), out int ct) ? ct : 0;

        var controls = def.Descendants("moduleControl")
            .Select(ParseModuleControl)
            .ToList();

        return new DnnModuleDefinition(friendly, cacheTime) { Controls = controls };
    }

    private static DnnModuleControl ParseModuleControl(XElement ctrl) =>
        new(
            Text(ctrl, "controlKey")  ?? string.Empty,
            Text(ctrl, "controlSrc")  ?? string.Empty,
            Text(ctrl, "controlType") ?? string.Empty,
            Text(ctrl, "helpUrl")     ?? string.Empty);

    // ------------------------------------------------------------------
    // Utility
    // ------------------------------------------------------------------

    /// <summary>Return trimmed text of the first matching child element, or null.</summary>
    private static string? Text(XElement parent, string localName)
    {
        string? v = parent.Descendants(localName).FirstOrDefault()?.Value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }
}
