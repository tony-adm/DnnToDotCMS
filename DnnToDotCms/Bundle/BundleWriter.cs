using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DnnToDotCms.Models;

namespace DnnToDotCms.Bundle;

/// <summary>
/// Writes a DotCMS-compatible push-publish bundle (.tar.gz) containing the
/// converted content types and, optionally, static theme assets extracted
/// from a DNN <c>export_themes.zip</c>.
/// </summary>
public static class BundleWriter
{
    // Fixed UUID of the built-in DotCMS "System Workflow".
    private const string SystemWorkflowId   = "d61a59e1-a49c-46f2-a929-db2b4bfa88b2";
    private const string SystemWorkflowName = "System Workflow";

    // File extensions considered "static assets" (safe to carry across).
    private static readonly HashSet<string> StaticExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".jpg", ".jpeg", ".png", ".gif", ".svg",
        ".ico", ".woff", ".woff2", ".ttf", ".eot", ".otf",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented          = false,
        PropertyNamingPolicy   = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Write a DotCMS site bundle tar.gz to <paramref name="output"/>.
    /// </summary>
    /// <param name="contentTypes">Converted DotCMS content types to include.</param>
    /// <param name="output">Target stream (receives gzip-compressed tar data).</param>
    /// <param name="themesZipPath">
    /// Optional path to a DNN <c>export_themes.zip</c>.  When provided,
    /// static assets (CSS, JS, images, fonts) are embedded in the bundle
    /// under a <c>themes/</c> directory for reference.
    /// </param>
    public static void Write(
        IReadOnlyList<DotCmsContentType> contentTypes,
        Stream output,
        string? themesZipPath = null)
    {
        string bundleId = Guid.NewGuid().ToString("N").ToUpperInvariant();

        var manifestEntries = new List<(string id, string name)>(contentTypes.Count);

        using var gz  = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
        using var tar = new TarWriter(gz, TarEntryFormat.Gnu);

        // --- content types ---------------------------------------------------
        foreach (DotCmsContentType ct in contentTypes)
        {
            string id    = Guid.NewGuid().ToString();
            string json  = BuildContentTypeJson(ct, id);
            WriteTextEntry(tar, $"working/System Host/{id}.contentType.json", json);
            manifestEntries.Add((id, ct.Name));
        }

        // --- manifest.csv ----------------------------------------------------
        WriteTextEntry(tar, "manifest.csv", BuildManifest(bundleId, manifestEntries));

        // --- theme static assets (optional) ----------------------------------
        if (themesZipPath is not null && File.Exists(themesZipPath))
        {
            try
            {
                WriteThemeAssets(tar, themesZipPath);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                // A corrupt or invalid themes zip is non-fatal: warn and continue.
                Console.Error.WriteLine(
                    $"Warning: Could not read theme assets from '{themesZipPath}': {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------
    // Content-type JSON builder
    // ------------------------------------------------------------------

    private static string BuildContentTypeJson(DotCmsContentType ct, string id)
    {
        // Make the content-type clazz Immutable-prefixed for bundle format.
        string ctClazz = ToImmutableClazz(ct.Clazz);

        var bundleContentType = new DotCmsBundleContentType
        {
            Clazz           = ctClazz,
            Name            = ct.Name,
            Id              = id,
            Description     = string.IsNullOrWhiteSpace(ct.Description) ? null : ct.Description,
            DefaultType     = ct.DefaultType,
            Fixed           = ct.Fixed,
            System          = ct.System,
            Variable        = ct.Variable,
            Icon            = ct.Icon,
        };

        var bundleFields = BuildBundleFields(ct.Fields, id);

        var entry = new DotCmsBundleEntry
        {
            ContentType        = bundleContentType,
            Fields             = bundleFields,
            WorkflowSchemaIds  = [SystemWorkflowId],
            WorkflowSchemaNames = [SystemWorkflowName],
        };

        return JsonSerializer.Serialize(entry, JsonOptions);
    }

    private static List<DotCmsBundleField> BuildBundleFields(
        IReadOnlyList<DotCmsField> fields, string contentTypeId)
    {
        var result = new List<DotCmsBundleField>(fields.Count + 2);

        // DotCMS bundles include layout fields (row + column) at the start.
        result.Add(MakeLayoutField("ImmutableRowField",    "fields-0", "fields0", 0, contentTypeId));
        result.Add(MakeLayoutField("ImmutableColumnField", "fields-1", "fields1", 1, contentTypeId));

        // Counters for dbColumn assignment (TEXT, LONG_TEXT, DATE, SYSTEM/binary)
        int textCount    = 0;
        int textAreaCount = 0;
        int dateCount    = 0;
        int binaryCount  = 0;

        for (int i = 0; i < fields.Count; i++)
        {
            DotCmsField f       = fields[i];
            string immutableClazz = ToImmutableClazz(f.Clazz);
            string dbColumn       = AssignDbColumn(f.DataType, ref textCount, ref textAreaCount,
                                                   ref dateCount, ref binaryCount);

            result.Add(new DotCmsBundleField
            {
                Clazz         = immutableClazz,
                Id            = Guid.NewGuid().ToString(),
                ContentTypeId = contentTypeId,
                Name          = f.Name,
                Variable      = f.Variable,
                SortOrder     = i + 2,       // +2 because layout fields occupy slots 0 and 1
                DataType      = f.DataType,
                DbColumn      = dbColumn,
                Indexed       = f.Indexed,
                Listed        = f.Listed,
                Required      = f.Required,
                Searchable    = f.Searchable,
                Fixed         = f.Fixed,
                ReadOnly      = f.ReadOnly,
                Unique        = f.Unique,
                Values        = f.Values,
                Hint          = f.Hint,
            });
        }

        return result;
    }

    private static DotCmsBundleField MakeLayoutField(
        string shortClazz, string name, string variable, int sortOrder, string contentTypeId) => new()
    {
        Clazz         = $"com.dotcms.contenttype.model.field.{shortClazz}",
        Id            = Guid.NewGuid().ToString(),
        ContentTypeId = contentTypeId,
        Name          = name,
        Variable      = variable,
        SortOrder     = sortOrder,
        DataType      = "SYSTEM",
        DbColumn      = "system_field",
    };

    // ------------------------------------------------------------------
    // Manifest builder
    // ------------------------------------------------------------------

    private static string BuildManifest(
        string bundleId, IReadOnlyList<(string id, string name)> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#Bundle ID:{bundleId}");
        sb.AppendLine("#Operation:PUBLISH");
        sb.AppendLine("INCLUDED/EXCLUDED,object type, Id, inode, title, site, folder, excluded by, reason to be evaluated");

        foreach (var (id, name) in entries)
            sb.AppendLine($"INCLUDED,contenttype,{id},,{name},SYSTEM_HOST,/,,Added by DNN to DotCMS converter");

        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Theme static-asset helper
    // ------------------------------------------------------------------

    private static void WriteThemeAssets(TarWriter tar, string themesZipPath)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(themesZipPath);

        foreach (var entry in zip.Entries)
        {
            // Only carry over recognised static file types.
            string ext = Path.GetExtension(entry.Name);
            if (string.IsNullOrEmpty(ext) || !StaticExtensions.Contains(ext))
                continue;

            // Build a clean relative path inside the bundle.
            // DNN stores skins under "_default/Skins/{ThemeName}/…"
            // → map to "themes/{ThemeName}/…" in the bundle.
            string entryPath = entry.FullName.Replace('\\', '/');
            string bundlePath = MapThemePath(entryPath);

            using var entryStream = entry.Open();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            ms.Position = 0;

            var tarEntry = new GnuTarEntry(TarEntryType.RegularFile, bundlePath)
            {
                DataStream = ms,
            };
            tar.WriteEntry(tarEntry);
        }
    }

    private static string MapThemePath(string zipEntryPath)
    {
        // "_default/Skins/Xcillion/Css/skin.css" → "themes/Xcillion/Css/skin.css"
        // "_default/Containers/Xcillion/…"       → "themes/Xcillion/Containers/…"
        const string skinsPrefix      = "_default/Skins/";
        const string containersPrefix = "_default/Containers/";

        if (zipEntryPath.StartsWith(skinsPrefix, StringComparison.OrdinalIgnoreCase))
            return "themes/" + zipEntryPath[skinsPrefix.Length..];

        if (zipEntryPath.StartsWith(containersPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Preserve container name but nest under a "Containers" sub-folder
            // to avoid conflicts with skin files sharing the same theme name.
            string rest = zipEntryPath[containersPrefix.Length..];
            int slash = rest.IndexOf('/');
            return slash < 0
                ? "themes/" + rest
                : $"themes/{rest[..slash]}/Containers/{rest[(slash + 1)..]}";
        }

        // Fallback: keep the original path prefixed with "themes/"
        return "themes/" + zipEntryPath;
    }

    // ------------------------------------------------------------------
    // Utility helpers
    // ------------------------------------------------------------------

    private static void WriteTextEntry(TarWriter tar, string path, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using var ms = new MemoryStream(bytes);
        var entry = new GnuTarEntry(TarEntryType.RegularFile, path)
        {
            DataStream = ms,
        };
        tar.WriteEntry(entry);
    }

    /// <summary>
    /// Converts a DotCMS class name to its Immutable variant required by the
    /// push-publish bundle format, e.g.
    /// <c>…model.field.TextField</c> → <c>…model.field.ImmutableTextField</c>.
    /// </summary>
    public static string ToImmutableClazz(string clazz)
    {
        int lastDot = clazz.LastIndexOf('.');
        if (lastDot < 0)
            return clazz;

        string prefix    = clazz[..(lastDot + 1)];
        string shortName = clazz[(lastDot + 1)..];

        return shortName.StartsWith("Immutable", StringComparison.Ordinal)
            ? clazz
            : prefix + "Immutable" + shortName;
    }

    /// <summary>
    /// Assigns the next available DotCMS database column name for a field
    /// based on its <paramref name="dataType"/>.
    /// </summary>
    private static string AssignDbColumn(
        string dataType,
        ref int textCount,
        ref int textAreaCount,
        ref int dateCount,
        ref int binaryCount) =>
        dataType switch
        {
            "LONG_TEXT" => $"text_area{++textAreaCount}",
            "DATE"      => $"date{++dateCount}",
            "SYSTEM"    => $"binary{++binaryCount}",
            _           => $"text{++textCount}",
        };
}
