using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
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
    {
        string folderPath;
        if (Directory.Exists(exportFolderOrJson))
        {
            folderPath = exportFolderOrJson;
        }
        else
        {
            string? parent = Path.GetDirectoryName(Path.GetFullPath(exportFolderOrJson));
            if (parent is null)
                return [];
            folderPath = parent;
        }

        string dbZipPath = Path.Combine(folderPath, "export_db.zip");
        if (!File.Exists(dbZipPath))
            return [];

        string tempDb = Path.GetTempFileName();
        try
        {
            // Extract export.dnndb (LiteDB binary) from the zip to a temp file.
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
            return ExtractHtmlContents(db);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                      or LiteException or InvalidOperationException)
        {
            Console.Error.WriteLine(
                $"Warning: Could not read HTML content from '{dbZipPath}': {ex.Message}");
            return [];
        }
        finally
        {
            try { File.Delete(tempDb); } catch (IOException) { /* best-effort cleanup */ }
        }
    }

    // ------------------------------------------------------------------
    // LiteDB HTML content extraction
    // ------------------------------------------------------------------

    private static IReadOnlyList<DnnHtmlContent> ExtractHtmlContents(LiteDatabase db)
    {
        // Build a mapping of ModuleID → display title from ExportTabModule.
        var moduleTitles = new Dictionary<int, string>();
        ILiteCollection<BsonDocument> tabModules = db.GetCollection("ExportTabModule");
        foreach (BsonDocument doc in tabModules.FindAll())
        {
            if (!doc.TryGetValue("ModuleID", out BsonValue moduleIdVal))
                continue;
            if (!doc.TryGetValue("ModuleTitle", out BsonValue titleVal))
                continue;

            string title = titleVal.AsString ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(title))
                moduleTitles[moduleIdVal.AsInt32] = title;
        }

        var results = new List<DnnHtmlContent>();
        ILiteCollection<BsonDocument> moduleContents = db.GetCollection("ExportModuleContent");

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

            moduleTitles.TryGetValue(moduleIdVal.AsInt32, out string? title);
            results.Add(new DnnHtmlContent(
                Title:   string.IsNullOrWhiteSpace(title) ? "Content" : title,
                HtmlBody: htmlBody));
        }

        return results;
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
            return string.IsNullOrWhiteSpace(decoded) ? null : decoded.Trim();
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
