using System.Text.Json.Serialization;

namespace DnnToDotCms.Models;

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
