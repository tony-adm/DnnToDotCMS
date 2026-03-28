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
