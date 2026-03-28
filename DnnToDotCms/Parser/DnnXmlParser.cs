using System.Xml.Linq;
using DnnToDotCms.Models;

namespace DnnToDotCms.Parser;

/// <summary>
/// Parses DNN (DotNetNuke) XML exports into <see cref="DnnModule"/> objects.
/// <para>
/// Two formats are supported:
/// <list type="bullet">
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
