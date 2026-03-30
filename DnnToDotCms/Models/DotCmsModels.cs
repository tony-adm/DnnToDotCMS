using System.Text.Json.Serialization;

namespace DnnToDotCms.Models;

// ---------------------------------------------------------------------------
// API-format models (used directly by DotCMS REST API: POST /api/v1/contenttype)
// ---------------------------------------------------------------------------

/// <summary>Represents a single field definition in a DotCMS content type.</summary>
public sealed class DotCmsField
{
    [JsonPropertyName("clazz")]
    public string Clazz { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("variable")]
    public string Variable { get; set; } = string.Empty;

    [JsonPropertyName("dataType")]
    public string DataType { get; set; } = "TEXT";

    [JsonPropertyName("fieldTypeLabel")]
    public string FieldTypeLabel { get; set; } = "Text";

    [JsonPropertyName("indexed")]
    public bool Indexed { get; set; } = true;

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("searchable")]
    public bool Searchable { get; set; } = true;

    [JsonPropertyName("sortable")]
    public bool Sortable { get; set; }

    [JsonPropertyName("listed")]
    public bool Listed { get; set; }

    [JsonPropertyName("fixed")]
    public bool Fixed { get; set; }

    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; set; }

    [JsonPropertyName("systemField")]
    public bool SystemField { get; set; }

    [JsonPropertyName("unique")]
    public bool Unique { get; set; }

    [JsonPropertyName("hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; set; }

    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Values { get; set; }
}

/// <summary>
/// Represents a DotCMS content type, ready to be serialised and sent to the
/// DotCMS REST API (<c>POST /api/v1/contenttype</c>).
/// </summary>
public sealed class DotCmsContentType
{
    [JsonPropertyName("clazz")]
    public string Clazz { get; set; } =
        "com.dotcms.contenttype.model.type.SimpleContentType";

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("variable")]
    public string Variable { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "fa fa-file";

    [JsonPropertyName("defaultType")]
    public bool DefaultType { get; set; }

    [JsonPropertyName("fixed")]
    public bool Fixed { get; set; }

    [JsonPropertyName("system")]
    public bool System { get; set; }

    [JsonPropertyName("fields")]
    public List<DotCmsField> Fields { get; set; } = [];
}

// ---------------------------------------------------------------------------
// Bundle-format models (DotCMS push-publish .tar.gz bundle format)
// ---------------------------------------------------------------------------

/// <summary>
/// Top-level wrapper for a content type entry inside a DotCMS push-publish
/// bundle file (<c>working/System Host/{id}.contentType.json</c>).
/// </summary>
public sealed class DotCmsBundleEntry
{
    [JsonPropertyName("contentType")]
    public DotCmsBundleContentType ContentType { get; set; } = new();

    [JsonPropertyName("fields")]
    public List<DotCmsBundleField> Fields { get; set; } = [];

    [JsonPropertyName("workflowSchemaIds")]
    public List<string> WorkflowSchemaIds { get; set; } = [];

    [JsonPropertyName("workflowSchemaNames")]
    public List<string> WorkflowSchemaNames { get; set; } = [];

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "PUBLISH";

    [JsonPropertyName("fieldVariables")]
    public List<object> FieldVariables { get; set; } = [];

    [JsonPropertyName("systemActionMappings")]
    public List<object> SystemActionMappings { get; set; } = [];
}

/// <summary>Content type metadata inside a DotCMS push-publish bundle entry.</summary>
public sealed class DotCmsBundleContentType
{
    [JsonPropertyName("clazz")]
    public string Clazz { get; set; } =
        "com.dotcms.contenttype.model.type.ImmutableSimpleContentType";

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("defaultType")]
    public bool DefaultType { get; set; }

    [JsonPropertyName("fixed")]
    public bool Fixed { get; set; }

    [JsonPropertyName("system")]
    public bool System { get; set; }

    [JsonPropertyName("versionable")]
    public bool Versionable { get; set; } = true;

    [JsonPropertyName("multilingualable")]
    public bool Multilingualable { get; set; }

    [JsonPropertyName("variable")]
    public string Variable { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; set; } = "SYSTEM_HOST";

    [JsonPropertyName("siteName")]
    public string SiteName { get; set; } = "systemHost";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "fa fa-file";

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }

    [JsonPropertyName("folder")]
    public string Folder { get; set; } = "SYSTEM_FOLDER";

    [JsonPropertyName("folderPath")]
    public string FolderPath { get; set; } = "/";
}

/// <summary>Field definition inside a DotCMS push-publish bundle entry.</summary>
public sealed class DotCmsBundleField
{
    [JsonPropertyName("clazz")]
    public string Clazz { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("contentTypeId")]
    public string ContentTypeId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("variable")]
    public string Variable { get; set; } = string.Empty;

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }

    [JsonPropertyName("dataType")]
    public string DataType { get; set; } = "TEXT";

    [JsonPropertyName("dbColumn")]
    public string DbColumn { get; set; } = string.Empty;

    [JsonPropertyName("indexed")]
    public bool Indexed { get; set; }

    [JsonPropertyName("listed")]
    public bool Listed { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("searchable")]
    public bool Searchable { get; set; }

    [JsonPropertyName("fixed")]
    public bool Fixed { get; set; }

    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; set; }

    [JsonPropertyName("unique")]
    public bool Unique { get; set; }

    [JsonPropertyName("forceIncludeInApi")]
    public bool ForceIncludeInApi { get; set; }

    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Values { get; set; }

    [JsonPropertyName("hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; set; }
}
