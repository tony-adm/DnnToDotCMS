using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    // Timestamp format used in all DotCMS XML bundle entries.
    private const string XmlTimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";


    /// <summary>
    /// Write a DotCMS site bundle tar.gz to <paramref name="output"/>.
    /// </summary>
    /// <param name="contentTypes">Converted DotCMS content types to include.</param>
    /// <param name="output">Target stream (receives gzip-compressed tar data).</param>
    /// <param name="themesZipPath">
    /// Optional path to a DNN <c>export_themes.zip</c>.  When provided,
    /// DNN containers (<c>_default/Containers/…/*.ascx</c>) are converted to
    /// DotCMS container JSON entries, DNN skins (<c>_default/Skins/…/*.ascx</c>)
    /// are converted to DotCMS template JSON entries, and remaining static
    /// assets (CSS, JS, images, fonts) are embedded under a <c>themes/</c>
    /// directory for reference.
    /// </param>
    public static void Write(
        IReadOnlyList<DotCmsContentType> contentTypes,
        Stream output,
        string? themesZipPath = null)
    {
        string bundleId = Guid.NewGuid().ToString("N").ToUpperInvariant();

        // Manifest entries: (objectType, id, name)
        var manifestEntries = new List<(string type, string id, string name)>(contentTypes.Count);

        // Pre-collect container and template definitions from the themes zip so
        // that their IDs are known before the manifest.csv is written.
        var containerDefs = new List<(string id, string name, string html)>();
        var templateDefs  = new List<(string id, string name, string html)>();

        if (themesZipPath is not null && File.Exists(themesZipPath))
        {
            try
            {
                CollectThemeDefinitions(themesZipPath, containerDefs, templateDefs);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Console.Error.WriteLine(
                    $"Warning: Could not read theme assets from '{themesZipPath}': {ex.Message}");
            }
        }

        using var gz  = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
        using var tar = new TarWriter(gz, TarEntryFormat.Gnu);

        // --- content types ---------------------------------------------------
        foreach (DotCmsContentType ct in contentTypes)
        {
            string id    = Guid.NewGuid().ToString();
            string json  = BuildContentTypeJson(ct, id);
            WriteTextEntry(tar, $"working/System Host/{id}.contentType.json", json);
            manifestEntries.Add(("contenttype", id, ct.Name));
        }

        // --- containers (from DNN containers) --------------------------------
        foreach (var (id, name, html) in containerDefs)
        {
            string inode = Guid.NewGuid().ToString();
            string xml   = BuildContainerXml(id, inode, name, html);
            WriteTextEntry(tar, $"working/System Host/{id}.containers.container.xml", xml);
            manifestEntries.Add(("containers", id, name));
        }

        // --- templates (from DNN skins) --------------------------------------
        foreach (var (id, name, html) in templateDefs)
        {
            string inode = Guid.NewGuid().ToString();
            string xml   = BuildTemplateXml(id, inode, name, html);
            WriteTextEntry(tar, $"working/System Host/{id}.template.template.xml", xml);
            manifestEntries.Add(("template", id, name));
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
    // Container XML builder  (matches ContainerWrapper format in dotCMS bundle)
    // ------------------------------------------------------------------

    private static string BuildContainerXml(
        string id, string inode, string title, string code)
    {
        string now      = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string xmlCode  = System.Security.SecurityElement.Escape(code) ?? string.Empty;
        string xmlTitle = System.Security.SecurityElement.Escape(title) ?? string.Empty;

        return $"""
            <com.dotcms.publisher.pusher.wrapper.ContainerWrapper>
              <containerId>
                <id>{id}</id>
                <assetName>{id}.containers</assetName>
                <assetType>containers</assetType>
                <parentPath>/</parentPath>
                <hostId>SYSTEM_HOST</hostId>
                <createDate class="sql-timestamp">{now}</createDate>
              </containerId>
              <container>
                <iDate class="sql-timestamp">{now}</iDate>
                <type>containers</type>
                <inode>{inode}</inode>
                <identifier>{id}</identifier>
                <source>DB</source>
                <title>{xmlTitle}</title>
                <friendlyName>{xmlTitle}</friendlyName>
                <modDate class="sql-timestamp">{now}</modDate>
                <modUser>dotcms.org.1</modUser>
                <sortOrder>0</sortOrder>
                <showOnMenu>false</showOnMenu>
                <code>{xmlCode}</code>
                <maxContentlets>10</maxContentlets>
                <useDiv>false</useDiv>
                <preLoop></preLoop>
                <postLoop></postLoop>
                <staticify>false</staticify>
                <notes>Converted from DNN container by DNN to DotCMS converter</notes>
              </container>
              <cvi class="com.dotmarketing.portlets.containers.model.ContainerVersionInfo">
                <identifier>{id}</identifier>
                <workingInode>{inode}</workingInode>
                <lockedOn class="sql-timestamp">{now}</lockedOn>
                <deleted>false</deleted>
                <versionTs class="sql-timestamp">{now}</versionTs>
              </cvi>
              <operation>PUBLISH</operation>
              <csList/>
            </com.dotcms.publisher.pusher.wrapper.ContainerWrapper>
            """;
    }

    // ------------------------------------------------------------------
    // Template XML builder  (matches TemplateWrapper format in dotCMS bundle)
    // ------------------------------------------------------------------

    private static string BuildTemplateXml(
        string id, string inode, string title, string body)
    {
        string now      = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string xmlBody  = System.Security.SecurityElement.Escape(body) ?? string.Empty;
        string xmlTitle = System.Security.SecurityElement.Escape(title) ?? string.Empty;

        return $"""
            <com.dotcms.publisher.pusher.wrapper.TemplateWrapper>
              <templateId>
                <id>{id}</id>
                <assetName>{id}.template</assetName>
                <assetType>template</assetType>
                <parentPath>/</parentPath>
                <hostId>SYSTEM_HOST</hostId>
                <owner></owner>
                <createDate class="sql-timestamp">{now}</createDate>
              </templateId>
              <template>
                <iDate class="sql-timestamp">{now}</iDate>
                <type>template</type>
                <owner></owner>
                <inode>{inode}</inode>
                <identifier>{id}</identifier>
                <source>DB</source>
                <title>{xmlTitle}</title>
                <friendlyName>{xmlTitle}</friendlyName>
                <modDate class="sql-timestamp">{now}</modDate>
                <modUser>dotcms.org.1</modUser>
                <sortOrder>0</sortOrder>
                <showOnMenu>false</showOnMenu>
                <body>{xmlBody}</body>
                <image></image>
                <drawed>false</drawed>
                <drawedBody>null</drawedBody>
                <countAddContainer>0</countAddContainer>
                <countContainers>0</countContainers>
                <theme></theme>
                <header>null</header>
                <footer>null</footer>
              </template>
              <vi class="com.dotmarketing.portlets.templates.model.TemplateVersionInfo">
                <identifier>{id}</identifier>
                <workingInode>{inode}</workingInode>
                <lockedOn class="sql-timestamp">{now}</lockedOn>
                <deleted>false</deleted>
                <versionTs class="sql-timestamp">{now}</versionTs>
              </vi>
              <operation>PUBLISH</operation>
            </com.dotcms.publisher.pusher.wrapper.TemplateWrapper>
            """;
    }

    // ------------------------------------------------------------------
    // Manifest builder
    // ------------------------------------------------------------------

    private static string BuildManifest(
        string bundleId, IReadOnlyList<(string type, string id, string name)> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#Bundle ID:{bundleId}");
        sb.AppendLine("#Operation:PUBLISH");
        sb.AppendLine("INCLUDED/EXCLUDED,object type, Id, inode, title, site, folder, excluded by, reason to be evaluated");

        foreach (var (type, id, name) in entries)
            sb.AppendLine($"INCLUDED,{type},{id},,{name},SYSTEM_HOST,/,,Added by DNN to DotCMS converter");

        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Theme definition collector — builds container/template defs
    // ------------------------------------------------------------------

    /// <summary>
    /// Scans <paramref name="themesZipPath"/> for DNN container and skin ASCX
    /// files and populates the two output lists with converted HTML.
    /// Only top-level ASCX files directly inside a theme folder are processed
    /// (e.g. <c>_default/Containers/Xcillion/Boxed.ascx</c>); sub-folder
    /// helpers such as <c>…/Common/AddFiles.ascx</c> are skipped.
    /// </summary>
    private static void CollectThemeDefinitions(
        string themesZipPath,
        List<(string id, string name, string html)> containerDefs,
        List<(string id, string name, string html)> templateDefs)
    {
        const string containersPrefix = "_default/Containers/";
        const string skinsPrefix      = "_default/Skins/";

        using var zip = ZipFile.OpenRead(themesZipPath);

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!entry.Name.EndsWith(".ascx", StringComparison.OrdinalIgnoreCase))
                continue;

            string entryPath = entry.FullName.Replace('\\', '/');

            if (entryPath.StartsWith(containersPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Only process ThemeName/ContainerName.ascx (exactly one slash after prefix)
                string rest = entryPath[containersPrefix.Length..];
                if (rest.Count(c => c == '/') != 1) continue;

                string name = Path.GetFileNameWithoutExtension(entry.Name);
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                string html = ConvertAscxToContainerHtml(reader.ReadToEnd());
                containerDefs.Add((Guid.NewGuid().ToString(), name, html));
            }
            else if (entryPath.StartsWith(skinsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Only process ThemeName/SkinName.ascx (exactly one slash after prefix)
                string rest = entryPath[skinsPrefix.Length..];
                if (rest.Count(c => c == '/') != 1) continue;

                string name = Path.GetFileNameWithoutExtension(entry.Name);
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                string html = ConvertAscxToTemplateHtml(reader.ReadToEnd());
                templateDefs.Add((Guid.NewGuid().ToString(), name, html));
            }
        }
    }

    // ------------------------------------------------------------------
    // ASCX → HTML conversion helpers
    // ------------------------------------------------------------------

    // Pre-compiled skin-control replacement pairs: (regex, replacement).
    // Each entry handles one well-known <dnn:TAGNAME .../> control.
    private static readonly (Regex Rx, string Replacement)[] SkinControlReplacements =
    [
        (new(@"<dnn:LOGO\s[^>]*/?>",        RegexOptions.IgnoreCase | RegexOptions.Singleline),
         @"<img src=""/logo.png"" alt=""Logo"" />"),
        (new(@"<dnn:MENU\s[^>]*/?>",        RegexOptions.IgnoreCase | RegexOptions.Singleline),
         "<!-- Navigation -->"),
        (new(@"<dnn:USER\s[^>]*/?>",        RegexOptions.IgnoreCase | RegexOptions.Singleline),
         "<!-- User Panel -->"),
        (new(@"<dnn:LOGIN\s[^>]*/?>",       RegexOptions.IgnoreCase | RegexOptions.Singleline),
         "<!-- Login -->"),
        (new(@"<dnn:SEARCH\s[^>]*/?>",      RegexOptions.IgnoreCase | RegexOptions.Singleline),
         "<!-- Search -->"),
        (new(@"<dnn:COPYRIGHT\s[^>]*/?>",   RegexOptions.IgnoreCase | RegexOptions.Singleline),
         "<!-- Copyright -->"),
        (new(@"<dnn:BREADCRUMB\s[^>]*/?>",  RegexOptions.IgnoreCase | RegexOptions.Singleline),
         "<!-- Breadcrumb -->"),
        (new(@"<dnn:CURRENTDATE\s[^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
         "<!-- Current Date -->"),
        (new(@"<dnn:LANGUAGE\s[^>]*/?>",    RegexOptions.IgnoreCase | RegexOptions.Singleline),
         "<!-- Language Selector -->"),
        (new(@"<dnn:TERMS\s[^>]*/?>",       RegexOptions.IgnoreCase | RegexOptions.Singleline),
         "<!-- Terms of Use -->"),
        (new(@"<dnn:PRIVACY\s[^>]*/?>",     RegexOptions.IgnoreCase | RegexOptions.Singleline),
         "<!-- Privacy -->"),
        // Controls to remove entirely (handled by DotCMS)
        (new(@"<dnn:STYLES\s[^>]*/?>",      RegexOptions.IgnoreCase | RegexOptions.Singleline),
         string.Empty),
        (new(@"<dnn:JQUERY\s[^>]*/?>",      RegexOptions.IgnoreCase | RegexOptions.Singleline),
         string.Empty),
        (new(@"<dnn:META\s[^>]*/?>",        RegexOptions.IgnoreCase | RegexOptions.Singleline),
         string.Empty),
        (new(@"<dnn:LINKTOMOBILE\s[^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
         string.Empty),
    ];

    // Matches <%@ ... %> ASP.NET directive blocks (possibly multi-line).
    private static readonly Regex DirectiveRegex =
        new(@"<%@[^%]*(?:%(?!>)[^%]*)*%>\s*\r?\n?", RegexOptions.Compiled | RegexOptions.Singleline);

    // Matches any inline ASP.NET code/expression block <% ... %>.
    private static readonly Regex CodeBlockRegex =
        new(@"<%[^@][^%]*(?:%(?!>)[^%]*)*%>", RegexOptions.Compiled | RegexOptions.Singleline);

    // Matches runat="server" (with optional surrounding whitespace).
    private static readonly Regex RunatServerRegex =
        new(@"\s+runat=""server""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches <dnn:TITLE .../> (self-closing DNN title control).
    private static readonly Regex DnnTitleRegex =
        new(@"<dnn:TITLE\s[^>]*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches the ContentPane div with optional runat/id attributes.
    private static readonly Regex ContentPaneRegex =
        new(@"<div\s+[^>]*id=""ContentPane""[^>]*>(\s*</div>)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Matches any remaining self-closing <dnn:TAGNAME .../> controls.
    private static readonly Regex DnnSelfClosingTagRegex =
        new(@"<dnn:[A-Za-z]+\s[^>]*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches any remaining open/close <dnn:TAGNAME> ... </dnn:TAGNAME> pairs.
    private static readonly Regex DnnOpenCloseTagRegex =
        new(@"</?dnn:[A-Za-z]+[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Converts a DNN container ASCX file into a DotCMS container Velocity
    /// template suitable for the <c>code</c> field of a container bundle entry.
    /// </summary>
    /// <remarks>
    /// Transformations applied:
    /// <list type="bullet">
    ///   <item>ASP.NET directive blocks (<c>&lt;%@ … %&gt;</c>) are removed.</item>
    ///   <item>Inline code blocks (<c>&lt;% … %&gt;</c>) are removed.</item>
    ///   <item><c>&lt;dnn:TITLE … /&gt;</c> is replaced with <c>$dotContent.title</c>.</item>
    ///   <item>The ContentPane div is replaced with <c>$!{dotContent.body}</c>.</item>
    ///   <item>All remaining <c>runat="server"</c> attributes are stripped.</item>
    ///   <item>Any other <c>&lt;dnn:…&gt;</c> controls are removed.</item>
    /// </list>
    /// </remarks>
    public static string ConvertAscxToContainerHtml(string ascx)
    {
        string html = DirectiveRegex.Replace(ascx, string.Empty);
        html = CodeBlockRegex.Replace(html, string.Empty);
        html = DnnTitleRegex.Replace(html, "$dotContent.title");
        html = ContentPaneRegex.Replace(html, "$!{dotContent.body}");
        html = RunatServerRegex.Replace(html, string.Empty);
        html = DnnSelfClosingTagRegex.Replace(html, string.Empty);
        html = DnnOpenCloseTagRegex.Replace(html, string.Empty);
        return html.Trim();
    }

    /// <summary>
    /// Converts a DNN skin ASCX file into a DotCMS template body suitable for
    /// the <c>body</c> field of a template bundle entry.
    /// </summary>
    /// <remarks>
    /// Transformations applied:
    /// <list type="bullet">
    ///   <item>ASP.NET directive blocks are removed.</item>
    ///   <item>Inline code blocks are removed.</item>
    ///   <item>
    ///     Known DNN skin controls are replaced with HTML equivalents:
    ///     <c>&lt;dnn:LOGO&gt;</c> → <c>&lt;img src="/logo.png" alt="Logo"/&gt;</c>,
    ///     <c>&lt;dnn:MENU&gt;</c> → <c>&lt;!-- Navigation --&gt;</c>, etc.
    ///   </item>
    ///   <item>All remaining <c>runat="server"</c> attributes are stripped.</item>
    ///   <item>Any unrecognised <c>&lt;dnn:…&gt;</c> controls are removed.</item>
    /// </list>
    /// </remarks>
    public static string ConvertAscxToTemplateHtml(string ascx)
    {
        string html = DirectiveRegex.Replace(ascx, string.Empty);
        html = CodeBlockRegex.Replace(html, string.Empty);

        // Replace well-known DNN skin controls with HTML/comment equivalents.
        foreach (var (rx, replacement) in SkinControlReplacements)
            html = rx.Replace(html, replacement);

        html = RunatServerRegex.Replace(html, string.Empty);
        html = DnnSelfClosingTagRegex.Replace(html, string.Empty);
        html = DnnOpenCloseTagRegex.Replace(html, string.Empty);
        return html.Trim();
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
