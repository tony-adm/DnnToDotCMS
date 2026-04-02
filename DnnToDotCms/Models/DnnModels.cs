namespace DnnToDotCms.Models;

/// <summary>Represents a single control (.ascx) within a DNN module definition.</summary>
public sealed record DnnModuleControl(
    string ControlKey,
    string ControlSrc,
    string ControlType,
    string HelpUrl = "");

/// <summary>A single module-definition block inside a DNN desktop-module element.</summary>
public sealed record DnnModuleDefinition(
    string FriendlyName,
    int DefaultCacheTime = 0)
{
    public IReadOnlyList<DnnModuleControl> Controls { get; init; } = [];
}

/// <summary>
/// Represents the HTML content of a single DNN HTML/Text module instance,
/// extracted from the LiteDB database in <c>export_db.zip</c>.
/// </summary>
public sealed record DnnHtmlContent(
    /// <summary>Display title for the content item (from the module title or tab name).</summary>
    string Title,
    /// <summary>Decoded HTML body text.</summary>
    string HtmlBody,
    /// <summary>
    /// The UniqueId GUID of the DNN tab (page) that this module belongs to.
    /// Empty when the tab association is unknown.
    /// </summary>
    string TabUniqueId = "",
    /// <summary>
    /// The DNN pane name that this module is placed in (e.g. <c>ContentPane</c>,
    /// <c>FooterLeft</c>).  Used to place content in the correct
    /// <c>#parseContainer</c> slot in the DotCMS template.  Empty when the
    /// pane association is unknown.
    /// </summary>
    string PaneName = "");

/// <summary>
/// Represents a DNN module parsed from either a .dnn package manifest or an
/// IPortable serialised module-content export.
/// </summary>
public sealed record DnnModule(
    string ModuleName,
    string FriendlyName = "",
    string Description = "",
    string FolderName = "",
    string BusinessControllerClass = "",
    string Version = "1.0.0")
{
    public IReadOnlyList<DnnModuleDefinition> Definitions { get; init; } = [];

    /// <summary>Raw content payload present in IPortable exports.</summary>
    public string? Content { get; init; }

    /// <summary>Arbitrary extra metadata carried through from the XML.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// Represents a DNN portal page (tab) extracted from the
/// <c>ExportTab</c> collection in <c>export_db.zip</c>.
/// </summary>
public sealed record DnnPortalPage(
    /// <summary>The DNN tab's stable identifier (UniqueId GUID).</summary>
    string UniqueId,
    /// <summary>Internal tab name used to build the URL slug.</summary>
    string Name,
    /// <summary>Display title shown in the browser title-bar.</summary>
    string Title,
    /// <summary>Page description (used for meta description).</summary>
    string Description,
    /// <summary>DNN tab path, e.g. <c>//Home</c> or <c>//ActivityFeed//MyProfile</c>.</summary>
    string TabPath,
    /// <summary>Nesting depth: 0 = top-level, 1 = first child, etc.</summary>
    int Level,
    /// <summary>Whether the page is visible in the navigation.</summary>
    bool IsVisible,
    /// <summary>
    /// DNN skin source, e.g. <c>[G]Skins/Xcillion/Home.ascx</c>.
    /// Empty when the page inherits the portal default.
    /// </summary>
    string SkinSrc);

/// <summary>
/// Represents a DNN portal file extracted from the <c>ExportFile</c>
/// collection in <c>export_db.zip</c> together with its binary content
/// from <c>export_files.zip</c>.
/// </summary>
public sealed record DnnPortalFile(
    /// <summary>Stable file identifier (UniqueId GUID from ExportFile).</summary>
    string UniqueId,
    /// <summary>Version/inode GUID (VersionGuid from ExportFile).</summary>
    string VersionGuid,
    /// <summary>File name, e.g. <c>logo.png</c>.</summary>
    string FileName,
    /// <summary>
    /// DNN folder path relative to the portal root, e.g. <c>""</c> for the
    /// root or <c>"Images/"</c> for the Images sub-folder.
    /// </summary>
    string FolderPath,
    /// <summary>MIME type reported by DNN, e.g. <c>image/png</c>.</summary>
    string MimeType,
    /// <summary>Raw file bytes.</summary>
    byte[] Content);
