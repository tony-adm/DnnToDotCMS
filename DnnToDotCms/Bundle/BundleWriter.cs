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
    // Fixed content-type UUID for the DotCMS built-in htmlpageasset content type.
    private const string HtmlPageAssetContentTypeId = "c541abb1-69b3-4bc5-8430-5e09e5239cc8";

    // Fixed content-type UUID for the DotCMS built-in FileAsset content type.
    private const string FileAssetContentTypeId = "33888b6f-7a8e-4069-b1b6-5c1aa9d0a48d";

    // Fixed UUID of the built-in DotCMS "System Workflow".
    private const string SystemWorkflowId   = "d61a59e1-a49c-46f2-a929-db2b4bfa88b2";
    private const string SystemWorkflowName = "System Workflow";

    // DotCMS stores several fields (content-type description, contentlet TEXT
    // fields, workflow task title, container/template title) in VARCHAR(255)
    // columns.  Any value exceeding this limit causes a database error on
    // bundle import.
    private const int MaxVarcharLength = 255;

    // Fixed content-type UUID for the DotCMS built-in Host/Site content type.
    private const string HostContentTypeInode = "855a2d72-f2f3-4169-8b04-ac5157c4380c";

    // Fixed workflow status UUID for the "Published" step in the DotCMS System Workflow.
    private const string SystemWorkflowPublishedStatus = "dc3c9cd0-8467-404b-bf95-cb7df3fbc293";
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
    /// DotCMS container XML entries, DNN skins (<c>_default/Skins/…/*.ascx</c>)
    /// are converted to DotCMS template XML entries, and remaining static
    /// assets (CSS, JS, images, fonts) are embedded under
    /// <c>ROOT/application/themes/</c> in the bundle so that DotCMS places
    /// them in the correct theme directory on import.
    /// </param>
    /// <param name="siteName">
    /// Optional DNN portal / site name (e.g. <c>"My Website"</c>).  When
    /// supplied a new DotCMS site (host) entry is written into the bundle so
    /// that importing the bundle creates the site.  Containers and templates
    /// derived from <paramref name="themesZipPath"/> are associated with the
    /// new site rather than with System Host.
    /// </param>
    /// <param name="htmlContents">
    /// Optional list of HTML module content items extracted from the DNN
    /// <c>export_db.zip</c> database.  When provided, each item is written
    /// as a published DotCMS contentlet (<c>PushContentWrapper</c>) linked to
    /// the <c>htmlContent</c> content type generated from the DNN HTML module.
    /// </param>
    /// <param name="pages">
    /// Optional list of DNN portal pages (tabs) extracted from the LiteDB
    /// database.  When provided, each page is written as a published DotCMS
    /// <c>htmlpageasset</c> contentlet.
    /// </param>
    /// <param name="portalFiles">
    /// Optional list of DNN portal static files extracted from
    /// <c>export_files.zip</c> together with their metadata from the LiteDB
    /// database.  When provided, each file is written as a published DotCMS
    /// <c>FileAsset</c> contentlet.
    /// </param>
    public static void Write(
        IReadOnlyList<DotCmsContentType> contentTypes,
        Stream output,
        string? themesZipPath = null,
        string? siteName = null,
        IReadOnlyList<DnnHtmlContent>? htmlContents = null,
        IReadOnlyList<DnnPortalPage>? pages = null,
        IReadOnlyList<DnnPortalFile>? portalFiles = null)
    {
        string bundleId = Guid.NewGuid().ToString("N").ToUpperInvariant();

        // Derive a URL-safe hostname from the optional site name.
        string? hostname = siteName is not null ? SanitizeHostname(siteName) : null;
        string? siteId   = hostname is not null ? Guid.NewGuid().ToString() : null;
        string? siteInode = hostname is not null ? Guid.NewGuid().ToString() : null;

        // Manifest entries: (objectType, identifier, inode, title, site, folder)
        // The site and folder columns follow DotCMS push-publish manifest conventions:
        //   • contenttype → site = "System Host", folder = "/"
        //   • DB containers / templates → site and folder are empty (DotCMS scans
        //     working/ subdirectories and uses the <hostId> in the XML instead)
        //   • host → site = "System Host", folder = "/"
        var manifestEntries = new List<(string type, string id, string inode, string name, string site, string folder)>(contentTypes.Count);

        // Pre-collect container and template definitions from the themes zip so
        // that their IDs are known before the manifest.csv is written.
        // Tuple: (identifier, inode, name, html, themeName)
        var containerDefs = new List<(string id, string inode, string name, string html)>();
        var templateDefs  = new List<(string id, string inode, string name, string html, string themeName)>();

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
        using var tar = new TarWriter(gz, TarEntryFormat.Ustar);

        // --- site / host entry (when a site name is provided) ----------------
        if (hostname is not null && siteId is not null && siteInode is not null)
        {
            string hostXml = BuildHostXml(siteId, siteInode, hostname);
            WriteTextEntry(tar, $"live/System Host/1/{siteId}.content.host.xml", hostXml);
            manifestEntries.Add(("host", siteId, siteInode, hostname, "System Host", "/"));
        }

        // Determine which host identifier and working-directory name to use
        // for content types, containers, and templates.  When a new site is
        // being created all three asset classes are placed under that site so
        // that DotCMS associates them correctly on import.
        string contentHostId  = siteId   ?? "SYSTEM_HOST";
        string contentWorkDir = hostname ?? "System Host";
        string contentSiteName = hostname ?? "systemHost";

        // --- content types ---------------------------------------------------
        // Track the UUID and variable assigned to the htmlContent type so that
        // HTML module contentlets can reference it via their stInode and assetSubType fields.
        string? htmlContentTypeId = null;
        string? htmlContentTypeVariable = null;
        foreach (DotCmsContentType ct in contentTypes)
        {
            string id   = Guid.NewGuid().ToString();
            string json = BuildContentTypeJson(ct, id, contentHostId, contentSiteName);
            // DotCMS push-publish format requires each .contentType.json to:
            //   1. contain two JSON objects (old state + new state), and
            //   2. appear twice in the tar archive.
            // For new content types the old and new states are identical.
            string doubledJson = json + json;
            WriteTextEntry(tar, $"working/{contentWorkDir}/{id}.contentType.json", doubledJson);
            WriteTextEntry(tar, $"working/{contentWorkDir}/{id}.contentType.json", doubledJson);
            manifestEntries.Add(("contenttype", id, "", ct.Name, contentWorkDir, "/"));

            if (ct.Variable == "htmlContent")
            {
                htmlContentTypeId = id;
                htmlContentTypeVariable = ct.Variable;
            }
        }

        // --- containers (from DNN containers) --------------------------------
        // Written to live/ so they are imported as published (not draft) assets.
        foreach (var (id, inode, name, html) in containerDefs)
        {
            string xml = BuildContainerXml(id, inode, name, html, contentHostId,
                htmlContentTypeId ?? string.Empty);
            WriteTextEntry(tar, $"live/{contentWorkDir}/{id}.containers.container.xml", xml);
            manifestEntries.Add(("containers", id, inode, name, "", ""));
        }

        // --- templates (from DNN skins) --------------------------------------
        // Written to live/ so they are imported as published (not draft) assets.
        foreach (var (id, inode, name, html, themeName) in templateDefs)
        {
            string xml = BuildTemplateXml(id, inode, name, html, contentHostId, themeName);
            WriteTextEntry(tar, $"live/{contentWorkDir}/{id}.template.template.xml", xml);
            manifestEntries.Add(("template", id, inode, name, "", ""));
        }

        // --- HTML contentlets (from DNN HTML modules) ------------------------
        // Pre-generate identifiers so they can be referenced in page multiTree entries.
        // Build a lookup: tab UniqueId → list of (contentlet identifier, container id).
        var tabContentMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (htmlContents is not null && htmlContents.Count > 0
            && htmlContentTypeId is not null && htmlContentTypeVariable is not null)
        {
            // Use the first available container for multiTree associations.
            string defaultContainerId = containerDefs.Count > 0 ? containerDefs[0].id : string.Empty;

            foreach (DnnHtmlContent hc in htmlContents)
            {
                string identifier = Guid.NewGuid().ToString();
                string inode      = Guid.NewGuid().ToString();

                string contentXml = BuildContentXml(
                    identifier, inode, hc.Title, hc.HtmlBody,
                    contentHostId, htmlContentTypeId, htmlContentTypeVariable);
                WriteTextEntry(
                    tar,
                    $"live/{contentWorkDir}/1/{identifier}.content.xml",
                    contentXml);

                string workflowXml = BuildContentWorkflowXml(identifier, hc.Title);
                WriteTextEntry(
                    tar,
                    $"live/{contentWorkDir}/1/{identifier}.contentworkflow.xml",
                    workflowXml);

                manifestEntries.Add(("contentlet", identifier, inode, hc.Title,
                    contentWorkDir, "/"));

                // Record the association so pages can reference this contentlet in multiTree.
                if (!string.IsNullOrEmpty(hc.TabUniqueId) && !string.IsNullOrEmpty(defaultContainerId))
                {
                    if (!tabContentMap.TryGetValue(hc.TabUniqueId, out List<string>? ids))
                    {
                        ids = [];
                        tabContentMap[hc.TabUniqueId] = ids;
                    }
                    ids.Add(identifier);
                }
            }
        }

        // --- portal pages (from DNN tabs) ------------------------------------
        if (pages is not null && pages.Count > 0)
        {
            // Build a lookup from skin name → template ID for matching DNN pages
            // to the templates that were converted from DNN skins.
            var skinToTemplateId = BuildSkinToTemplateMap(templateDefs);

            // Use the first available container for multiTree associations.
            string defaultContainerId = containerDefs.Count > 0 ? containerDefs[0].id : string.Empty;

            foreach (DnnPortalPage page in pages)
            {
                // Only import non-deleted, non-admin Level-0 pages.
                if (page.Level != 0) continue;
                if (page.Name.Equals("Admin", StringComparison.OrdinalIgnoreCase)) continue;

                string identifier = Guid.NewGuid().ToString();
                string inode      = Guid.NewGuid().ToString();
                string url        = PageNameToUrl(page.Name);
                string templateId = ResolveTemplateId(page.SkinSrc, skinToTemplateId);

                // Resolve the HTML contentlet identifiers belonging to this page.
                tabContentMap.TryGetValue(page.UniqueId, out List<string>? pageContentIds);

                string pageXml = BuildPageXml(
                    identifier, inode, page.Title, url,
                    contentHostId, templateId,
                    defaultContainerId, pageContentIds ?? []);
                WriteTextEntry(
                    tar,
                    $"live/{contentWorkDir}/1/{identifier}.content.xml",
                    pageXml);

                string workflowXml = BuildContentWorkflowXml(identifier, page.Title);
                WriteTextEntry(
                    tar,
                    $"live/{contentWorkDir}/1/{identifier}.contentworkflow.xml",
                    workflowXml);

                manifestEntries.Add(("contentlet", identifier, inode,
                    Truncate(page.Title, MaxVarcharLength), contentWorkDir, "/"));
            }
        }

        // --- portal static files (from DNN export_files.zip) -----------------
        if (portalFiles is not null && portalFiles.Count > 0)
        {
            // Build a lookup from DNN folder path to DotCMS folder inode/identifier.
            // Sub-folders (non-empty FolderPath) need explicit FolderWrapper XML
            // entries so DotCMS creates them on import.  Root-level files use
            // SYSTEM_FOLDER (the site root).
            // For nested paths (e.g. "Menus/SubFolder/") every intermediate
            // directory must also be present so DotCMS's parent-path check
            // (identifier_parent_path_check) succeeds.
            var folderInodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DnnPortalFile pf in portalFiles)
            {
                if (string.IsNullOrEmpty(pf.FolderPath))
                    continue;

                string trimmed = pf.FolderPath.TrimEnd('/');
                string[] parts = trimmed.Split('/');
                for (int depth = 1; depth <= parts.Length; depth++)
                {
                    string partialPath = string.Join("/", parts[..depth]) + "/";
                    folderInodes.TryAdd(partialPath, Guid.NewGuid().ToString());
                }
            }

            // Write folder XML entries for each unique sub-folder so DotCMS
            // creates the folder structure before placing file assets inside.
            if (siteId is not null && siteInode is not null)
            {
                foreach (var (dnnFolderPath, folderInode) in folderInodes)
                {
                    string folderName   = dnnFolderPath.TrimEnd('/').Split('/').Last();
                    string dotcmsPath   = "/" + dnnFolderPath.TrimStart('/');
                    if (!dotcmsPath.EndsWith('/')) dotcmsPath += '/';
                    // Compute the real parent path so DotCMS's
                    // identifier_parent_path_check constraint is satisfied.
                    string trimmedDir   = dnnFolderPath.TrimEnd('/');
                    int lastSlash       = trimmedDir.LastIndexOf('/');
                    string parentPath   = lastSlash >= 0
                        ? "/" + trimmedDir[..lastSlash] + "/"
                        : "/";
                    string folderXml    = BuildFolderXml(
                        folderInode, folderName, dotcmsPath, parentPath,
                        siteId, siteInode);
                    // Place the .folder.xml entry in a nested directory
                    // that mirrors the folder hierarchy so DotCMS's
                    // FolderHandler (which sorts by file-path length)
                    // processes parent folders before their children.
                    string entryDir = parentPath == "/"
                        ? "ROOT"
                        : "ROOT/" + parentPath.Trim('/');
                    WriteTextEntry(tar, $"{entryDir}/{folderInode}.folder.xml", folderXml);
                    manifestEntries.Add(("folder", folderInode, folderInode, dotcmsPath, contentWorkDir, "/"));
                }
            }

            foreach (DnnPortalFile pf in portalFiles)
            {
                string identifier = pf.UniqueId;
                string inode      = pf.VersionGuid;

                // Resolve the folder inode: root files use SYSTEM_FOLDER; sub-folder
                // files use the pre-generated inode for that folder.
                // Normalise the key to include a trailing slash to match the
                // dictionary which always stores paths with a trailing slash.
                string normalizedKey = string.IsNullOrEmpty(pf.FolderPath) ? ""
                    : pf.FolderPath.TrimEnd('/') + "/";
                string folderInode = string.IsNullOrEmpty(pf.FolderPath)
                    ? "SYSTEM_FOLDER"
                    : (folderInodes.TryGetValue(normalizedKey, out string? fi) ? fi : "SYSTEM_FOLDER");

                // Write the actual file bytes under assets/{x}/{y}/{inode}/fileAsset/{filename}
                string assetPath = BuildAssetPath(inode, pf.FileName);
                WriteBinaryEntry(tar, assetPath, pf.Content);

                // Write the contentlet XML.
                string fileContentXml = BuildFileAssetXml(
                    identifier, inode, pf.FileName, assetPath,
                    contentHostId, pf.FolderPath, folderInode);
                WriteTextEntry(
                    tar,
                    $"live/{contentWorkDir}/1/{identifier}.content.xml",
                    fileContentXml);

                string workflowXml = BuildContentWorkflowXml(identifier, pf.FileName);
                WriteTextEntry(
                    tar,
                    $"live/{contentWorkDir}/1/{identifier}.contentworkflow.xml",
                    workflowXml);

                manifestEntries.Add(("contentlet", identifier, inode,
                    Truncate(pf.FileName, MaxVarcharLength), contentWorkDir, "/"));

                // Files from the DNN Images/ folder are also written as static web
                // resources to ROOT/application/images/ so that DotCMS serves them
                // directly from /application/images/.  HTML content references the
                // same path after the {{PortalRoot}}Images/ token replacement.
                // Normalise the folder path to ensure a trailing slash so that the
                // prefix check works whether DNN stored "Images" or "Images/".
                const string imagesFolderPrefix = "Images/";
                string normalizedFolderPath = pf.FolderPath;
                if (normalizedFolderPath.Length > 0 && !normalizedFolderPath.EndsWith('/'))
                    normalizedFolderPath += '/';
                if (normalizedFolderPath.StartsWith(imagesFolderPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // Preserve any sub-folder depth (e.g. "Images/Banners/" → "Banners/").
                    string relativePath = normalizedFolderPath[imagesFolderPrefix.Length..] + pf.FileName;
                    WriteBinaryEntry(tar, "ROOT/application/images/" + relativePath, pf.Content);
                }
            }
        }

        // --- theme static files as FileAsset contentlets (optional) ----------
        // Theme static files (CSS, JS, images, fonts) from export_themes.zip
        // must be written as proper DotCMS FileAsset contentlets with folder
        // hierarchy XML so that DotCMS's push-publish importer creates the
        // /application/themes/ directory structure on the target server.
        // Raw files under ROOT/ are NOT automatically imported; they require
        // full contentlet metadata (content XML + workflow XML + binary asset)
        // plus FolderWrapper XML entries for each directory level.
        if (themesZipPath is not null && File.Exists(themesZipPath))
        {
            try
            {
                WriteThemeFileAssets(
                    tar, themesZipPath, contentHostId,
                    siteId, siteInode, contentWorkDir, manifestEntries);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Console.Error.WriteLine(
                    $"Warning: Could not read theme assets from '{themesZipPath}': {ex.Message}");
            }
        }

        // --- manifest.csv ----------------------------------------------------
        WriteTextEntry(tar, "manifest.csv", BuildManifest(bundleId, manifestEntries));
    }

    // ------------------------------------------------------------------
    // Content-type JSON builder
    // ------------------------------------------------------------------

    private static string BuildContentTypeJson(
        DotCmsContentType ct,
        string id,
        string hostId   = "SYSTEM_HOST",
        string siteName = "systemHost")
    {
        // Make the content-type clazz Immutable-prefixed for bundle format.
        string ctClazz = ToImmutableClazz(ct.Clazz);

        var bundleContentType = new DotCmsBundleContentType
        {
            Clazz           = ctClazz,
            Name            = ct.Name.Trim(),
            Id              = id,
            Description     = string.IsNullOrWhiteSpace(ct.Description)
                                  ? null
                                  : Truncate(ct.Description, MaxVarcharLength),
            DefaultType     = ct.DefaultType,
            Fixed           = ct.Fixed,
            System          = ct.System,
            Variable        = ct.Variable,
            Icon            = ct.Icon,
            Host            = hostId,
            SiteName        = siteName,
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
        string id, string inode, string title, string code, string hostId = "SYSTEM_HOST",
        string htmlContentTypeId = "")
    {
        string now      = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string xmlCode  = System.Security.SecurityElement.Escape(code) ?? string.Empty;
        string xmlTitle = System.Security.SecurityElement.Escape(Truncate(title, MaxVarcharLength)) ?? string.Empty;

        // Build the csList element that associates this container with the htmlContent
        // content type.  Without this, DotCMS cannot connect the container to content
        // items of that type, so the page renders empty.  The csList code uses the
        // direct Velocity field variables ($!{title}, $!{body}) rather than the legacy
        // $dotContent accessor that is only available in the container's <code> field.
        string csListXml;
        if (!string.IsNullOrEmpty(htmlContentTypeId))
        {
            string csId     = Guid.NewGuid().ToString();
            string csCode   = TransformToCsListCode(code);
            string xmlCsCode = System.Security.SecurityElement.Escape(csCode) ?? string.Empty;
            csListXml = $"""
                  <com.dotmarketing.beans.ContainerStructure>
                    <id>{csId}</id>
                    <structureId>{htmlContentTypeId}</structureId>
                    <containerInode>{inode}</containerInode>
                    <containerId>{id}</containerId>
                    <code>{xmlCsCode}</code>
                  </com.dotmarketing.beans.ContainerStructure>
                """;
        }
        else
        {
            csListXml = string.Empty;
        }

        return $"""
            <com.dotcms.publisher.pusher.wrapper.ContainerWrapper>
              <containerId>
                <id>{id}</id>
                <assetName>{id}.containers</assetName>
                <assetType>containers</assetType>
                <parentPath>/</parentPath>
                <hostId>{hostId}</hostId>
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
                <liveInode>{inode}</liveInode>
                <workingInode>{inode}</workingInode>
                <lockedOn class="sql-timestamp">{now}</lockedOn>
                <deleted>false</deleted>
                <versionTs class="sql-timestamp">{now}</versionTs>
              </cvi>
              <operation>PUBLISH</operation>
              <csList>
            {csListXml}
              </csList>
            </com.dotcms.publisher.pusher.wrapper.ContainerWrapper>
            """;
    }

    /// <summary>
    /// Transforms legacy <c>$dotContent.field</c> and <c>$!{dotContent.field}</c>
    /// Velocity variable syntax into the direct <c>$!{field}</c> form used inside
    /// container <c>csList</c> rendering code.
    /// </summary>
    private static string TransformToCsListCode(string code)
    {
        // $!{dotContent.fieldName} → $!{fieldName}
        string result = Regex.Replace(code, @"\$!\{dotContent\.(\w+)\}", "$!{$1}");
        // $dotContent.fieldName → $!{fieldName}
        result = Regex.Replace(result, @"\$dotContent\.(\w+)", "$!{$1}");
        return result;
    }

    // ------------------------------------------------------------------
    // Template XML builder  (matches TemplateWrapper format in dotCMS bundle)
    // ------------------------------------------------------------------

    private static string BuildTemplateXml(
        string id, string inode, string title, string body, string hostId = "SYSTEM_HOST",
        string themeName = "")
    {
        string now      = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string xmlBody  = System.Security.SecurityElement.Escape(body) ?? string.Empty;
        string xmlTitle = System.Security.SecurityElement.Escape(Truncate(title, MaxVarcharLength)) ?? string.Empty;
        // Resolve the DotCMS application theme path.  Static assets from the DNN
        // skin are written to ROOT/application/themes/{themeName}/ so the template
        // must reference that same path for DotCMS to include the theme's CSS/JS.
        string themeValue = string.IsNullOrWhiteSpace(themeName)
            ? string.Empty
            : $"/application/themes/{themeName}";

        return $"""
            <com.dotcms.publisher.pusher.wrapper.TemplateWrapper>
              <templateId>
                <id>{id}</id>
                <assetName>{id}.template</assetName>
                <assetType>template</assetType>
                <parentPath>/</parentPath>
                <hostId>{hostId}</hostId>
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
                <theme>{themeValue}</theme>
                <header>null</header>
                <footer>null</footer>
              </template>
              <vi class="com.dotmarketing.portlets.templates.model.TemplateVersionInfo">
                <identifier>{id}</identifier>
                <liveInode>{inode}</liveInode>
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
    // Host / site XML builder  (matches HostWrapper format in dotCMS bundle)
    // ------------------------------------------------------------------

    /// <summary>
    /// One empty <c>ConcurrentHashMap.Segment</c> for the Java 7 serialization
    /// format used inside a DotCMS <c>HostWrapper</c> XML.
    /// <para>
    /// <b>Why this exact structure?</b>
    /// DotCMS uses XStream to serialize/deserialize push-publish bundles.
    /// A DotCMS <c>Host</c> object extends <c>Contentlet</c> whose field map
    /// is a <c>Contentlet.ContentletHashMap</c> — a subclass of
    /// <c>ConcurrentHashMap</c>.  XStream falls back to Java's own serialization
    /// mechanism for this class (hence <c>serialization="custom"</c>).
    /// In Java 7, <c>ConcurrentHashMap.writeObject</c> first calls
    /// <c>defaultWriteObject</c>, which writes the non-transient fields
    /// (<c>segments</c>, <c>segmentShift</c>, <c>segmentMask</c>), then writes
    /// all live entries as alternating key/value objects, and finally writes
    /// two <c>null</c> sentinels.  XStream wraps the <c>defaultWriteObject</c>
    /// output in a <c>&lt;default&gt;</c> block and the explicit writes come
    /// directly after it inside the enclosing <c>&lt;concurrent-hash-map&gt;</c>
    /// element.  Do <b>not</b> simplify or reorder this structure — DotCMS's
    /// XStream deserializer will fail silently and the host will not be created.
    /// </para>
    /// </summary>
    private const string EmptyConcurrentHashMapSegment = """
              <java.util.concurrent.ConcurrentHashMap_-Segment>
                <sync class="java.util.concurrent.locks.ReentrantLock$NonfairSync" serialization="custom">
                  <java.util.concurrent.locks.AbstractQueuedSynchronizer>
                    <default>
                      <state>0</state>
                    </default>
                  </java.util.concurrent.locks.AbstractQueuedSynchronizer>
                  <java.util.concurrent.locks.ReentrantLock_-Sync>
                    <default/>
                  </java.util.concurrent.locks.ReentrantLock_-Sync>
                </sync>
                <loadFactor>0.75</loadFactor>
              </java.util.concurrent.ConcurrentHashMap_-Segment>
        """;

    private static string BuildHostXml(string hostId, string hostInode, string hostname)
    {
        string now         = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string xmlHostname = System.Security.SecurityElement.Escape(hostname) ?? string.Empty;

        // Java 7 ConcurrentHashMap uses 16 segments by default (concurrencyLevel=16,
        // which gives 2^4 = 16 segments).  segmentShift=28 and segmentMask=15
        // correspond to this configuration.  All 16 segments are empty because the
        // actual map entries are written after the <default> block (see
        // EmptyConcurrentHashMapSegment documentation above).
        string segments = string.Concat(Enumerable.Repeat(EmptyConcurrentHashMapSegment, 16));

        return $"""
            <com.dotcms.publisher.pusher.wrapper.HostWrapper>
              <info>
                <identifier>{hostId}</identifier>
                <liveInode>{hostInode}</liveInode>
                <workingInode>{hostInode}</workingInode>
                <lockedBy>dotcms.org.1</lockedBy>
                <lockedOn class="sql-timestamp">{now}</lockedOn>
                <deleted>false</deleted>
                <versionTs class="sql-timestamp">{now}</versionTs>
                <lang>1</lang>
                <variant>DEFAULT</variant>
                <publishDate class="sql-timestamp">{now}</publishDate>
              </info>
              <host>
                <map class="com.dotmarketing.portlets.contentlet.model.Contentlet$ContentletHashMap" serialization="custom">
                  <unserializable-parents/>
                  <concurrent-hash-map>
                    <default>
                      <segments>
            {segments}
                      </segments>
                      <segmentShift>28</segmentShift>
                      <segmentMask>15</segmentMask>
                    </default>
                    <string>type</string>
                    <string>host</string>
                    <string>inode</string>
                    <string>{hostInode}</string>
                    <string>hostname</string>
                    <string>{xmlHostname}</string>
                    <string>hostName</string>
                    <string>{xmlHostname}</string>
                    <string>__DOTNAME__</string>
                    <string>{xmlHostname}</string>
                    <string>host</string>
                    <string>SYSTEM_HOST</string>
                    <string>stInode</string>
                    <string>{HostContentTypeInode}</string>
                    <string>owner</string>
                    <string>dotcms.org.1</string>
                    <string>identifier</string>
                    <string>{hostId}</string>
                    <string>languageId</string>
                    <long>1</long>
                    <string>runDashboard</string>
                    <boolean>false</boolean>
                    <string>isSystemHost</string>
                    <boolean>false</boolean>
                    <string>isDefault</string>
                    <boolean>false</boolean>
                    <string>folder</string>
                    <string>SYSTEM_FOLDER</string>
                    <string>tagStorage</string>
                    <string>SYSTEM_HOST</string>
                    <string>sortOrder</string>
                    <long>0</long>
                    <string>modUser</string>
                    <string>dotcms.org.1</string>
                    <string>open</string>
                    <boolean>true</boolean>
                    <null/>
                    <null/>
                  </concurrent-hash-map>
                  <com.dotmarketing.portlets.contentlet.model.Contentlet_-ContentletHashMap>
                    <default>
                      <outer-class reference="../../../.."/>
                    </default>
                  </com.dotmarketing.portlets.contentlet.model.Contentlet_-ContentletHashMap>
                </map>
                <lowIndexPriority>false</lowIndexPriority>
                <variantId>DEFAULT</variantId>
              </host>
              <id>
                <id>{hostId}</id>
                <assetName>{hostId}.content</assetName>
                <assetType>contentlet</assetType>
                <parentPath>/</parentPath>
                <hostId>SYSTEM_HOST</hostId>
                <owner>dotcms.org.1</owner>
                <createDate class="sql-timestamp">{now}</createDate>
                <assetSubType>Host</assetSubType>
              </id>
              <multiTree/>
              <tree/>
              <tags class="java.util.ImmutableCollections$ListN" resolves-to="java.util.CollSer" serialization="custom">
                <java.util.CollSer>
                  <default>
                    <tag>1</tag>
                  </default>
                  <int>0</int>
                </java.util.CollSer>
              </tags>
              <operation>PUBLISH</operation>
            </com.dotcms.publisher.pusher.wrapper.HostWrapper>
            """;
    }

    // ------------------------------------------------------------------
    // Contentlet (PushContentWrapper) XML builder
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a DotCMS <c>PushContentWrapper</c> XML for an HTML contentlet.
    /// The content map uses the Java 7 <c>ConcurrentHashMap</c> XStream
    /// serialization format (identical to <see cref="BuildHostXml"/>).
    /// </summary>
    private static string BuildContentXml(
        string identifier,
        string inode,
        string title,
        string htmlBody,
        string hostId,
        string contentTypeId,
        string contentTypeVariable)
    {
        string now        = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string xmlTitle   = System.Security.SecurityElement.Escape(Truncate(title, MaxVarcharLength))   ?? string.Empty;
        string xmlBody    = System.Security.SecurityElement.Escape(htmlBody) ?? string.Empty;

        // Java 7 ConcurrentHashMap with 16 empty segments (same pattern as HostWrapper).
        string segments = string.Concat(Enumerable.Repeat(EmptyConcurrentHashMapSegment, 16));

        return $"""
            <com.dotcms.publisher.pusher.wrapper.PushContentWrapper>
              <info>
                <identifier>{identifier}</identifier>
                <liveInode>{inode}</liveInode>
                <workingInode>{inode}</workingInode>
                <lockedOn class="sql-timestamp">{now}</lockedOn>
                <deleted>false</deleted>
                <versionTs class="sql-timestamp">{now}</versionTs>
                <lang>1</lang>
                <variant>DEFAULT</variant>
                <publishDate class="sql-timestamp">{now}</publishDate>
              </info>
              <content>
                <map class="com.dotmarketing.portlets.contentlet.model.Contentlet$ContentletHashMap" serialization="custom">
                  <unserializable-parents/>
                  <concurrent-hash-map>
                    <default>
                      <segments>
            {segments}
                      </segments>
                      <segmentShift>28</segmentShift>
                      <segmentMask>15</segmentMask>
                    </default>
                    <string>modDate</string>
                    <sql-timestamp>{now}</sql-timestamp>
                    <string>inode</string>
                    <string>{inode}</string>
                    <string>disabledWYSIWYG</string>
                    <com.google.common.collect.RegularImmutableList resolves-to="com.google.common.collect.ImmutableList$SerializedForm">
                      <elements/>
                    </com.google.common.collect.RegularImmutableList>
                    <string>host</string>
                    <string>{hostId}</string>
                    <string>stInode</string>
                    <string>{contentTypeId}</string>
                    <string>title</string>
                    <string>{xmlTitle}</string>
                    <string>body</string>
                    <string>{xmlBody}</string>
                    <string>owner</string>
                    <string>dotcms.org.1</string>
                    <string>identifier</string>
                    <string>{identifier}</string>
                    <string>nullProperties</string>
                    <java.util.concurrent.ConcurrentHashMap_-KeySetView>
                      <map/>
                      <value class="boolean">true</value>
                    </java.util.concurrent.ConcurrentHashMap_-KeySetView>
                    <string>languageId</string>
                    <long>1</long>
                    <string>folder</string>
                    <string>SYSTEM_FOLDER</string>
                    <string>sortOrder</string>
                    <long>0</long>
                    <string>modUser</string>
                    <string>dotcms.org.1</string>
                    <null/>
                    <null/>
                  </concurrent-hash-map>
                  <com.dotmarketing.portlets.contentlet.model.Contentlet_-ContentletHashMap>
                    <default>
                      <outer-class reference="../../../.."/>
                    </default>
                  </com.dotmarketing.portlets.contentlet.model.Contentlet_-ContentletHashMap>
                </map>
                <lowIndexPriority>false</lowIndexPriority>
                <variantId>DEFAULT</variantId>
              </content>
              <id>
                <id>{identifier}</id>
                <assetName>{identifier}.content</assetName>
                <assetType>contentlet</assetType>
                <parentPath>/</parentPath>
                <hostId>{hostId}</hostId>
                <owner>dotcms.org.1</owner>
                <createDate class="sql-timestamp">{now}</createDate>
                <assetSubType>{contentTypeVariable}</assetSubType>
              </id>
              <multiTree/>
              <tree/>
              <categories/>
              <tags class="java.util.ImmutableCollections$ListN" resolves-to="java.util.CollSer" serialization="custom">
                <java.util.CollSer>
                  <default>
                    <tag>1</tag>
                  </default>
                  <int>0</int>
                </java.util.CollSer>
              </tags>
              <operation>PUBLISH</operation>
              <language>
                <id>1</id>
                <languageCode>en</languageCode>
                <countryCode>US</countryCode>
                <language>English</language>
                <country>United States</country>
                <isoCode>en-us</isoCode>
              </language>
              <contentTags/>
              <contentletMetadata/>
            </com.dotcms.publisher.pusher.wrapper.PushContentWrapper>
            """;
    }

    // ------------------------------------------------------------------
    // Content workflow (PushContentWorkflowWrapper) XML builder
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a DotCMS <c>PushContentWorkflowWrapper</c> XML that places the
    /// contentlet in the "Published" step of the System Workflow.
    /// </summary>
    private static string BuildContentWorkflowXml(string identifier, string title)
    {
        string now      = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string xmlTitle = System.Security.SecurityElement.Escape(Truncate(title, MaxVarcharLength)) ?? string.Empty;

        return $"""
            <com.dotcms.publisher.pusher.wrapper.PushContentWorkflowWrapper>
              <task>
                <id>{Guid.NewGuid()}</id>
                <creationDate class="sql-timestamp">{now}</creationDate>
                <modDate class="sql-timestamp">{now}</modDate>
                <createdBy>dotcms.org.1</createdBy>
                <title>{xmlTitle}</title>
                <status>{SystemWorkflowPublishedStatus}</status>
                <webasset>{identifier}</webasset>
                <languageId>1</languageId>
              </task>
              <history class="com.google.common.collect.RegularImmutableList" resolves-to="com.google.common.collect.ImmutableList$SerializedForm">
                <elements/>
              </history>
              <comments class="com.google.common.collect.RegularImmutableList" reference="../history"/>
            </com.dotcms.publisher.pusher.wrapper.PushContentWorkflowWrapper>
            """;
    }

    // ------------------------------------------------------------------
    // Manifest builder
    // ------------------------------------------------------------------

    private static string BuildManifest(
        string bundleId,
        IReadOnlyList<(string type, string id, string inode, string name, string site, string folder)> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#Bundle ID:{bundleId}");
        sb.AppendLine("#Operation:PUBLISH");
        sb.AppendLine("INCLUDED/EXCLUDED,object type, Id, inode, title, site, folder, excluded by, reason to be evaluated");

        foreach (var (type, id, inode, name, site, folder) in entries)
            sb.AppendLine($"INCLUDED,{type},{id},{inode},{name},{site},{folder},,Added by DNN to DotCMS converter");

        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Page (htmlpageasset) XML builder
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a DotCMS <c>PushContentWrapper</c> XML for an
    /// <c>htmlpageasset</c> contentlet (a DotCMS page).
    /// </summary>
    private static string BuildPageXml(
        string identifier,
        string inode,
        string title,
        string url,
        string hostId,
        string templateId,
        string containerId = "",
        IReadOnlyList<string>? contentletIds = null)
    {
        string now      = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string xmlTitle = System.Security.SecurityElement.Escape(Truncate(title, MaxVarcharLength)) ?? string.Empty;
        string xmlUrl   = System.Security.SecurityElement.Escape(url) ?? string.Empty;

        string segments = string.Concat(Enumerable.Repeat(EmptyConcurrentHashMapSegment, 16));

        // Build multiTree entries linking this page to its contentlets via the container.
        string multiTreeContent = BuildMultiTreeXml(identifier, containerId, contentletIds ?? []);
        string multiTreeElement = string.IsNullOrEmpty(multiTreeContent)
            ? "<multiTree/>"
            : $"<multiTree>{multiTreeContent}</multiTree>";

        return $"""
            <com.dotcms.publisher.pusher.wrapper.PushContentWrapper>
              <info>
                <identifier>{identifier}</identifier>
                <liveInode>{inode}</liveInode>
                <workingInode>{inode}</workingInode>
                <lockedOn class="sql-timestamp">{now}</lockedOn>
                <deleted>false</deleted>
                <versionTs class="sql-timestamp">{now}</versionTs>
                <lang>1</lang>
                <variant>DEFAULT</variant>
                <publishDate class="sql-timestamp">{now}</publishDate>
              </info>
              <content>
                <map class="com.dotmarketing.portlets.contentlet.model.Contentlet$ContentletHashMap" serialization="custom">
                  <unserializable-parents/>
                  <concurrent-hash-map>
                    <default>
                      <segments>
            {segments}
                      </segments>
                      <segmentShift>28</segmentShift>
                      <segmentMask>15</segmentMask>
                    </default>
                    <string>modDate</string>
                    <sql-timestamp>{now}</sql-timestamp>
                    <string>cachettl</string>
                    <string>0</string>
                    <string>inode</string>
                    <string>{inode}</string>
                    <string>disabledWYSIWYG</string>
                    <com.google.common.collect.RegularImmutableList resolves-to="com.google.common.collect.ImmutableList$SerializedForm">
                      <elements/>
                    </com.google.common.collect.RegularImmutableList>
                    <string>host</string>
                    <string>{hostId}</string>
                    <string>stInode</string>
                    <string>{HtmlPageAssetContentTypeId}</string>
                    <string>title</string>
                    <string>{xmlTitle}</string>
                    <string>friendlyName</string>
                    <string>{xmlTitle}</string>
                    <string>template</string>
                    <string>{templateId}</string>
                    <string>url</string>
                    <string>{xmlUrl}</string>
                    <string>owner</string>
                    <string>dotcms.org.1</string>
                    <string>identifier</string>
                    <string>{identifier}</string>
                    <string>nullProperties</string>
                    <java.util.concurrent.ConcurrentHashMap_-KeySetView>
                      <map/>
                      <value class="boolean">true</value>
                    </java.util.concurrent.ConcurrentHashMap_-KeySetView>
                    <string>languageId</string>
                    <long>1</long>
                    <string>folder</string>
                    <string>SYSTEM_FOLDER</string>
                    <string>sortOrder</string>
                    <long>0</long>
                    <string>modUser</string>
                    <string>dotcms.org.1</string>
                    <null/>
                    <null/>
                  </concurrent-hash-map>
                  <com.dotmarketing.portlets.contentlet.model.Contentlet_-ContentletHashMap>
                    <default>
                      <outer-class reference="../../../.."/>
                    </default>
                  </com.dotmarketing.portlets.contentlet.model.Contentlet_-ContentletHashMap>
                </map>
                <lowIndexPriority>false</lowIndexPriority>
                <variantId>DEFAULT</variantId>
              </content>
              <id>
                <id>{identifier}</id>
                <assetName>{xmlUrl}</assetName>
                <assetType>contentlet</assetType>
                <parentPath>/</parentPath>
                <hostId>{hostId}</hostId>
                <owner>dotcms.org.1</owner>
                <createDate class="sql-timestamp">{now}</createDate>
                <assetSubType>htmlpageasset</assetSubType>
              </id>
              {multiTreeElement}
              <tree/>
              <categories/>
              <tags class="java.util.ImmutableCollections$ListN" resolves-to="java.util.CollSer" serialization="custom">
                <java.util.CollSer>
                  <default>
                    <tag>1</tag>
                  </default>
                  <int>0</int>
                </java.util.CollSer>
              </tags>
              <operation>PUBLISH</operation>
              <language>
                <id>1</id>
                <languageCode>en</languageCode>
                <countryCode>US</countryCode>
                <language>English</language>
                <country>United States</country>
                <isoCode>en-us</isoCode>
              </language>
              <contentTags/>
              <contentletMetadata/>
            </com.dotcms.publisher.pusher.wrapper.PushContentWrapper>
            """;
    }

    // ------------------------------------------------------------------
    // MultiTree XML builder
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds zero or more DotCMS <c>com.dotmarketing.beans.MultiTree</c> XML
    /// elements that associate a page (<paramref name="pageId"/>) with
    /// <paramref name="contentletIds"/> placed in <paramref name="containerId"/>.
    /// Returns an empty string when no container or contentlet IDs are given.
    /// </summary>
    private static string BuildMultiTreeXml(
        string pageId,
        string containerId,
        IReadOnlyList<string> contentletIds)
    {
        if (string.IsNullOrEmpty(containerId) || contentletIds.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        for (int i = 0; i < contentletIds.Count; i++)
        {
            // The relation_type must match the second argument of the corresponding
            // #parseContainer directive in the template body, which uses sequential
            // integers starting at 1.  Using a random GUID would leave the container
            // slots empty when DotCMS renders the page.
            string relationType = (i + 1).ToString();
            sb.Append($"""

                  <map>
                    <entry>
                      <string>parent1</string>
                      <string>{pageId}</string>
                    </entry>
                    <entry>
                      <string>parent2</string>
                      <string>{containerId}</string>
                    </entry>
                    <entry>
                      <string>child</string>
                      <string>{contentletIds[i]}</string>
                    </entry>
                    <entry>
                      <string>relation_type</string>
                      <string>{relationType}</string>
                    </entry>
                    <entry>
                      <string>tree_order</string>
                      <int>{i}</int>
                    </entry>
                    <entry>
                      <string>personalization</string>
                      <string>dot:default</string>
                    </entry>
                    <entry>
                      <string>variantId</string>
                      <string>DEFAULT</string>
                    </entry>
                  </map>
              """);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // FileAsset (PushContentWrapper) XML builder
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a DotCMS <c>PushContentWrapper</c> XML for a <c>FileAsset</c>
    /// contentlet representing a portal static file.
    /// </summary>
    private static string BuildFileAssetXml(
        string identifier,
        string inode,
        string fileName,
        string assetPath,
        string hostId,
        string folderPath = "",
        string folderInode = "SYSTEM_FOLDER")
    {
        string now          = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string xmlFileName  = System.Security.SecurityElement.Escape(fileName)  ?? string.Empty;
        // The fileAsset field uses the on-disk storage path under /data/shared.
        string storagePath  = $"/data/shared/{assetPath}";

        // Build the DotCMS parentPath from the DNN folder path.
        // DNN uses relative paths like "" (root) or "Images/" (sub-folder).
        // DotCMS parentPath must start and end with "/" so:  "" → "/"  "Images/" → "/Images/"
        string parentPath = "/" + folderPath.TrimStart('/');
        if (!parentPath.EndsWith('/'))
            parentPath += '/';

        string segments = string.Concat(Enumerable.Repeat(EmptyConcurrentHashMapSegment, 16));

        return $"""
            <com.dotcms.publisher.pusher.wrapper.PushContentWrapper>
              <info>
                <identifier>{identifier}</identifier>
                <liveInode>{inode}</liveInode>
                <workingInode>{inode}</workingInode>
                <lockedOn class="sql-timestamp">{now}</lockedOn>
                <deleted>false</deleted>
                <versionTs class="sql-timestamp">{now}</versionTs>
                <lang>1</lang>
                <variant>DEFAULT</variant>
                <publishDate class="sql-timestamp">{now}</publishDate>
              </info>
              <content>
                <map class="com.dotmarketing.portlets.contentlet.model.Contentlet$ContentletHashMap" serialization="custom">
                  <unserializable-parents/>
                  <concurrent-hash-map>
                    <default>
                      <segments>
            {segments}
                      </segments>
                      <segmentShift>28</segmentShift>
                      <segmentMask>15</segmentMask>
                    </default>
                    <string>modDate</string>
                    <sql-timestamp>{now}</sql-timestamp>
                    <string>fileName</string>
                    <string>{xmlFileName}</string>
                    <string>title</string>
                    <string>{xmlFileName}</string>
                    <string>inode</string>
                    <string>{inode}</string>
                    <string>disabledWYSIWYG</string>
                    <com.google.common.collect.RegularImmutableList resolves-to="com.google.common.collect.ImmutableList$SerializedForm">
                      <elements/>
                    </com.google.common.collect.RegularImmutableList>
                    <string>host</string>
                    <string>{hostId}</string>
                    <string>stInode</string>
                    <string>{FileAssetContentTypeId}</string>
                    <string>owner</string>
                    <string>dotcms.org.1</string>
                    <string>identifier</string>
                    <string>{identifier}</string>
                    <string>nullProperties</string>
                    <java.util.concurrent.ConcurrentHashMap_-KeySetView>
                      <map/>
                      <value class="boolean">true</value>
                     </java.util.concurrent.ConcurrentHashMap_-KeySetView>
                    <string>languageId</string>
                    <long>1</long>
                    <string>fileAsset</string>
                    <file>{storagePath}</file>
                    <string>folder</string>
                    <string>{folderInode}</string>
                    <string>sortOrder</string>
                    <long>0</long>
                    <string>modUser</string>
                    <string>dotcms.org.1</string>
                    <null/>
                    <null/>
                  </concurrent-hash-map>
                  <com.dotmarketing.portlets.contentlet.model.Contentlet_-ContentletHashMap>
                    <default>
                      <outer-class reference="../../../.."/>
                    </default>
                  </com.dotmarketing.portlets.contentlet.model.Contentlet_-ContentletHashMap>
                </map>
                <lowIndexPriority>false</lowIndexPriority>
                <variantId>DEFAULT</variantId>
              </content>
              <id>
                <id>{identifier}</id>
                <assetName>{xmlFileName}</assetName>
                <assetType>contentlet</assetType>
                <parentPath>{parentPath}</parentPath>
                <hostId>{hostId}</hostId>
                <owner>dotcms.org.1</owner>
                <createDate class="sql-timestamp">{now}</createDate>
                <assetSubType>FileAsset</assetSubType>
              </id>
              <multiTree/>
              <tree/>
              <categories/>
              <tags class="java.util.ImmutableCollections$ListN" resolves-to="java.util.CollSer" serialization="custom">
                <java.util.CollSer>
                  <default>
                    <tag>1</tag>
                  </default>
                  <int>0</int>
                </java.util.CollSer>
              </tags>
              <operation>PUBLISH</operation>
              <language>
                <id>1</id>
                <languageCode>en</languageCode>
                <countryCode>US</countryCode>
                <language>English</language>
                <country>United States</country>
                <isoCode>en-us</isoCode>
              </language>
              <contentTags/>
              <contentletMetadata/>
            </com.dotcms.publisher.pusher.wrapper.PushContentWrapper>
            """;
    }

    // ------------------------------------------------------------------
    // FolderWrapper XML builder
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a DotCMS <c>FolderWrapper</c> XML for a site sub-folder.
    /// DotCMS's push-publish handler (FolderHandler) reads these entries and
    /// creates the folder on the receiving server before importing any file
    /// assets that belong in it.
    /// </summary>
    /// <param name="folderInode">UUID used as both inode and identifier for the folder.</param>
    /// <param name="folderName">Simple folder name, e.g. <c>Images</c>.</param>
    /// <param name="folderPath">Full path including leading and trailing slash, e.g. <c>/Images/</c>.</param>
    /// <param name="parentPath">Parent path, <c>/</c> for a top-level folder.</param>
    /// <param name="hostId">Identifier of the target site.</param>
    /// <param name="hostInode">Inode of the target site (used in the host sub-element).</param>
    private static string BuildFolderXml(
        string folderInode,
        string folderName,
        string folderPath,
        string parentPath,
        string hostId,
        string hostInode)
    {
        string now      = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string segments = string.Concat(Enumerable.Repeat(EmptyConcurrentHashMapSegment, 16));

        return $"""
            <com.dotcms.publisher.pusher.wrapper.FolderWrapper>
              <folder>
                <inode>{folderInode}</inode>
                <owner>dotcms.org.1</owner>
                <iDate class="sql-timestamp">{now}</iDate>
                <type>folder</type>
                <identifier>{folderInode}</identifier>
                <name>{folderName}</name>
                <title>{folderName}</title>
                <hostId>{hostId}</hostId>
                <showOnMenu>false</showOnMenu>
                <sortOrder>0</sortOrder>
                <filesMasks></filesMasks>
                <defaultFileType>{FileAssetContentTypeId}</defaultFileType>
                <modDate class="sql-timestamp">{now}</modDate>
                <modUser>dotcms.org.1</modUser>
              </folder>
              <folderId>
                <id>{folderInode}</id>
                <assetName>{folderName}</assetName>
                <assetType>folder</assetType>
                <parentPath>{parentPath}</parentPath>
                <hostId>{hostId}</hostId>
                <owner>dotcms.org.1</owner>
                <createDate class="sql-timestamp">{now}</createDate>
                <path>{folderPath}</path>
              </folderId>
              <host>
                <map class="com.dotmarketing.portlets.contentlet.model.Contentlet$ContentletHashMap" serialization="custom">
                  <unserializable-parents/>
                  <concurrent-hash-map>
                    <default>
                      <segments>
            {segments}
                      </segments>
                      <segmentShift>28</segmentShift>
                      <segmentMask>15</segmentMask>
                    </default>
                    <string>type</string>
                    <string>host</string>
                    <string>identifier</string>
                    <string>{hostId}</string>
                    <null/>
                    <null/>
                  </concurrent-hash-map>
                  <com.dotmarketing.portlets.contentlet.model.Contentlet_-ContentletHashMap>
                    <default>
                      <outer-class reference="../../../.."/>
                    </default>
                  </com.dotmarketing.portlets.contentlet.model.Contentlet_-ContentletHashMap>
                </map>
                <lowIndexPriority>false</lowIndexPriority>
                <variantId>DEFAULT</variantId>
              </host>
              <hostId>
                <id>{hostId}</id>
                <assetName>{hostInode}.content</assetName>
                <assetType>contentlet</assetType>
                <parentPath>/</parentPath>
                <hostId>SYSTEM_HOST</hostId>
                <owner>dotcms.org.1</owner>
                <createDate class="sql-timestamp">{now}</createDate>
                <assetSubType>Host</assetSubType>
              </hostId>
              <operation>PUBLISH</operation>
            </com.dotcms.publisher.pusher.wrapper.FolderWrapper>
            """;
    }

    // ------------------------------------------------------------------
    // Page / file helper utilities
    // ------------------------------------------------------------------

    /// <summary>
    /// Converts a DNN page name to a URL-safe slug (lowercase, hyphens).
    /// </summary>
    private static string PageNameToUrl(string name)
    {
        string slug = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "page" : slug;
    }

    /// <summary>
    /// Returns the tar path for a file asset's binary content:
    /// <c>assets/{x}/{y}/{inode}/fileAsset/{filename}</c>,
    /// where <c>x</c> and <c>y</c> are the first two characters of the inode.
    /// </summary>
    private static string BuildAssetPath(string inode, string fileName)
    {
        string clean  = inode.Replace("-", "");
        char   x      = clean.Length > 0 ? clean[0] : '0';
        char   y      = clean.Length > 1 ? clean[1] : '0';
        return $"assets/{x}/{y}/{inode}/fileAsset/{fileName}";
    }

    /// <summary>
    /// Builds a skin-name → template-ID lookup from the template definitions
    /// collected from the themes zip.  Skin name is derived from the ASCX file
    /// name without extension (e.g. <c>Home</c> from <c>Home.ascx</c>).
    /// </summary>
    private static Dictionary<string, string> BuildSkinToTemplateMap(
        IReadOnlyList<(string id, string inode, string name, string html, string themeName)> templateDefs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, _, name, _, _) in templateDefs)
            map.TryAdd(name, id);
        return map;
    }

    /// <summary>
    /// Resolves the DotCMS template ID for a DNN page based on its
    /// <paramref name="skinSrc"/> (e.g. <c>[G]Skins/Xcillion/Home.ascx</c>).
    /// Falls back to the first available template, or empty string when none.
    /// </summary>
    private static string ResolveTemplateId(
        string skinSrc,
        Dictionary<string, string> skinToTemplateId)
    {
        if (!string.IsNullOrWhiteSpace(skinSrc))
        {
            // Extract the skin file name without extension.
            string skinName = Path.GetFileNameWithoutExtension(skinSrc);
            if (skinToTemplateId.TryGetValue(skinName, out string? id))
                return id;
        }
        // Fall back to the first available template.
        foreach (string id in skinToTemplateId.Values)
            return id;
        return string.Empty;
    }



    /// <summary>
    /// Scans <paramref name="themesZipPath"/> for DNN container and skin ASCX
    /// files and populates the two output lists with converted HTML.
    /// Only top-level ASCX files directly inside a theme folder are processed
    /// (e.g. <c>_default/Containers/Xcillion/Boxed.ascx</c>); sub-folder
    /// helpers such as <c>…/Common/AddFiles.ascx</c> are skipped.
    /// </summary>
    /// <remarks>
    /// Containers are collected in a first pass so that their identifiers are
    /// known before template conversion begins.  The first container's ID is
    /// passed to <see cref="ConvertAscxToTemplateHtml"/> so that DNN skin pane
    /// divs (<c>&lt;div runat="server"&gt;</c>) are replaced with
    /// <c>#parseContainer</c> directives that DotCMS can evaluate at render time.
    /// </remarks>
    private static void CollectThemeDefinitions(
        string themesZipPath,
        List<(string id, string inode, string name, string html)> containerDefs,
        List<(string id, string inode, string name, string html, string themeName)> templateDefs)
    {
        const string containersPrefix = "_default/Containers/";
        const string skinsPrefix      = "_default/Skins/";

        using var zip = ZipFile.OpenRead(themesZipPath);

        // First pass: collect containers so their IDs are available for template conversion.
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!entry.Name.EndsWith(".ascx", StringComparison.OrdinalIgnoreCase))
                continue;

            string entryPath = entry.FullName.Replace('\\', '/');

            if (!entryPath.StartsWith(containersPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Only process ThemeName/ContainerName.ascx (exactly one slash after prefix)
            string rest = entryPath[containersPrefix.Length..];
            if (rest.Count(c => c == '/') != 1) continue;

            string name = Path.GetFileNameWithoutExtension(entry.Name);
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            string html = ConvertAscxToContainerHtml(reader.ReadToEnd());
            containerDefs.Add((Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), name, html));
        }

        // Use the first container's identifier so template panes can reference it.
        string firstContainerId = containerDefs.Count > 0 ? containerDefs[0].id : string.Empty;

        // Second pass: collect templates, wiring pane divs to #parseContainer.
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!entry.Name.EndsWith(".ascx", StringComparison.OrdinalIgnoreCase))
                continue;

            string entryPath = entry.FullName.Replace('\\', '/');

            if (!entryPath.StartsWith(skinsPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Only process ThemeName/SkinName.ascx (exactly one slash after prefix)
            string rest = entryPath[skinsPrefix.Length..];
            if (rest.Count(c => c == '/') != 1) continue;

            // Extract the theme name (the directory between skinsPrefix and SkinName.ascx).
            int slash = rest.IndexOf('/');
            string themeName = slash >= 0 ? rest[..slash] : string.Empty;

            string name = Path.GetFileNameWithoutExtension(entry.Name);
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            string ascx = reader.ReadToEnd();

            // Resolve <!--#include file="..."--> SSI directives by inlining
            // the referenced file content from the same zip archive.
            int lastSlashIdx = entryPath.LastIndexOf('/');
            if (lastSlashIdx > 0)
            {
                string skinDir = entryPath[..lastSlashIdx];
                ascx = ResolveSsiIncludes(ascx, skinDir, zip);
            }

            string html = ConvertAscxToTemplateHtml(ascx, firstContainerId, themeName, name);
            templateDefs.Add((Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), name, html, themeName));
        }
    }

    // Regex matching <!--#include file="..." --> SSI directives.
    private static readonly Regex SsiIncludeRegex =
        new(@"<!--\s*#include\s+file=""([^""]+)""\s*-->",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Resolves <c>&lt;!--#include file="..."--&gt;</c> Server-Side Include
    /// directives by reading the referenced file from <paramref name="zip"/>
    /// and inlining its content.  Paths are resolved relative to
    /// <paramref name="skinDir"/> (the directory containing the ASCX file).
    /// </summary>
    public static string ResolveSsiIncludes(
        string ascx, string skinDir, ZipArchive zip)
    {
        return SsiIncludeRegex.Replace(ascx, match =>
        {
            string relPath = match.Groups[1].Value.Replace('\\', '/');
            string fullPath = skinDir + "/" + relPath;

            // Look for the entry using case-insensitive comparison because
            // DNN on Windows is case-insensitive.
            ZipArchiveEntry? included = zip.Entries.FirstOrDefault(e =>
                string.Equals(
                    e.FullName.Replace('\\', '/'),
                    fullPath,
                    StringComparison.OrdinalIgnoreCase));

            if (included is null)
            {
                Console.Error.WriteLine(
                    $"Warning: SSI include target not found in themes zip: '{fullPath}'");
                return string.Empty;
            }

            using var reader = new StreamReader(included.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    // ------------------------------------------------------------------
    // ASCX → HTML conversion helpers
    // ------------------------------------------------------------------

    // Pre-compiled skin-control replacement pairs: (regex, replacement).
    // Each entry handles one well-known <dnn:TAGNAME .../> control.
    // Note: <dnn:LOGO> is handled separately in ConvertAscxToTemplateHtml so it can
    // reference the theme-specific logo path when a theme name is available.
    private static readonly (Regex Rx, string Replacement)[] SkinControlReplacements =
    [
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

    // Matches any <div ... runat="server" ...> pane element in a DNN skin (possibly self-closing).
    // Used in template conversion to replace server-side panes with #parseContainer calls.
    private static readonly Regex DnnRunatPaneDivRegex =
        new(@"<div\s+[^>]*runat=""server""[^>]*>(\s*</div>)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Matches any remaining self-closing <dnn:TAGNAME .../> controls.
    private static readonly Regex DnnSelfClosingTagRegex =
        new(@"<dnn:[A-Za-z]+\s[^>]*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches any remaining open/close <dnn:TAGNAME> ... </dnn:TAGNAME> pairs.
    private static readonly Regex DnnOpenCloseTagRegex =
        new(@"</?dnn:[A-Za-z]+[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches <dnn:DnnCssInclude ... FilePath="..." .../> and captures the FilePath value.
    private static readonly Regex DnnCssIncludeRegex =
        new(@"<dnn:DnnCssInclude\s[^>]*FilePath=""([^""]+)""[^>]*/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Matches <dnn:DnnJsInclude ... FilePath="..." .../> and captures the FilePath value.
    private static readonly Regex DnnJsIncludeRegex =
        new(@"<dnn:DnnJsInclude\s[^>]*FilePath=""([^""]+)""[^>]*/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

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

    // Matches a <dnn:LOGO .../> control (possibly self-closing or with closing tag).
    private static readonly Regex DnnLogoRegex =
        new(@"<dnn:LOGO\s[^>]*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Converts a DNN skin ASCX file into a DotCMS template body suitable for
    /// the <c>body</c> field of a template bundle entry.
    /// </summary>
    /// <param name="ascx">Raw content of the DNN skin <c>.ascx</c> file.</param>
    /// <param name="defaultContainerId">
    /// Optional DotCMS container identifier.  When provided, DNN server-side
    /// pane <c>&lt;div&gt;</c> elements (those with <c>runat="server"</c>) are
    /// replaced with <c>#parseContainer('{id}', 'N')</c> Velocity directives so
    /// that DotCMS renders the associated container content on the page.  When
    /// omitted, pane divs are removed.
    /// </param>
    /// <param name="themeName">
    /// Optional theme name (e.g. <c>Xcillion</c>).  When provided:
    /// <list type="bullet">
    ///   <item>
    ///     A <c>&lt;link&gt;</c> tag for the theme's <c>skin.css</c> file is
    ///     prepended to the template body so that DotCMS loads the skin styles.
    ///   </item>
    ///   <item>
    ///     <c>&lt;dnn:LOGO&gt;</c> is replaced with an <c>&lt;img&gt;</c> tag
    ///     referencing <c>/application/themes/{themeName}/Images/logo.png</c>
    ///     rather than the generic <c>/logo.png</c> path.
    ///   </item>
    /// </list>
    /// </param>
    /// <remarks>
    /// Transformations applied:
    /// <list type="bullet">
    ///   <item>ASP.NET directive blocks are removed.</item>
    ///   <item>Inline code blocks are removed.</item>
    ///   <item>
    ///     Known DNN skin controls are replaced with HTML equivalents:
    ///     <c>&lt;dnn:LOGO&gt;</c> → <c>&lt;img src="/application/themes/{themeName}/Images/logo.png" alt="Logo"/&gt;</c>,
    ///     <c>&lt;dnn:MENU&gt;</c> → <c>&lt;!-- Navigation --&gt;</c>, etc.
    ///   </item>
    ///   <item>
    ///     DNN server-side pane <c>&lt;div runat="server"&gt;</c> elements are
    ///     replaced with <c>#parseContainer</c> Velocity directives (or removed
    ///     when no container ID is available).
    ///   </item>
    ///   <item>All remaining <c>runat="server"</c> attributes are stripped.</item>
    ///   <item>Any unrecognised <c>&lt;dnn:…&gt;</c> controls are removed.</item>
    /// </list>
    /// </remarks>
    public static string ConvertAscxToTemplateHtml(
        string ascx,
        string defaultContainerId = "",
        string themeName = "",
        string skinName = "")
    {
        string html = DirectiveRegex.Replace(ascx, string.Empty);
        html = CodeBlockRegex.Replace(html, string.Empty);

        // Replace <dnn:LOGO> with a theme-aware img tag.
        // When a theme name is known, use the theme's Images/logo.png path;
        // otherwise fall back to the generic /logo.png site-root placeholder.
        string logoSrc = string.IsNullOrWhiteSpace(themeName)
            ? "/logo.png"
            : $"/application/themes/{themeName}/Images/logo.png";
        html = DnnLogoRegex.Replace(html, $@"<img src=""{logoSrc}"" alt=""Logo"" />");

        // Replace well-known DNN skin controls with HTML/comment equivalents.
        foreach (var (rx, replacement) in SkinControlReplacements)
            html = rx.Replace(html, replacement);

        // Convert <dnn:DnnCssInclude FilePath="..." /> and <dnn:DnnJsInclude
        // FilePath="..." /> to standard HTML <link>/<script> tags.  The
        // FilePath attribute is relative to the skin folder, which maps to
        // /application/themes/{themeName}/ in DotCMS.  This must run before
        // the generic DNN-tag cleanup that follows.
        if (!string.IsNullOrWhiteSpace(themeName))
        {
            string themeBase = $"/application/themes/{themeName}/";
            html = DnnCssIncludeRegex.Replace(html,
                m => $@"<link rel=""stylesheet"" href=""{themeBase}{m.Groups[1].Value}"" />");
            html = DnnJsIncludeRegex.Replace(html,
                m => $@"<script src=""{themeBase}{m.Groups[1].Value}""></script>");
        }

        // Replace DNN server-side pane divs (runat="server") with #parseContainer
        // directives so DotCMS renders container content in those zones.
        // This must run before RunatServerRegex strips the runat attribute.
        if (!string.IsNullOrEmpty(defaultContainerId))
        {
            int uuid = 0;
            html = DnnRunatPaneDivRegex.Replace(html,
                _ => $"#parseContainer('{defaultContainerId}', '{++uuid}')");
        }
        else
        {
            // No container available: remove pane divs entirely.
            html = DnnRunatPaneDivRegex.Replace(html, string.Empty);
        }

        html = RunatServerRegex.Replace(html, string.Empty);
        html = DnnSelfClosingTagRegex.Replace(html, string.Empty);
        html = DnnOpenCloseTagRegex.Replace(html, string.Empty);
        html = html.Trim();

        // Prepend a <link> tag for the theme's main skin.css so that DotCMS
        // loads the skin styles when rendering the page.  DNN automatically
        // included the skin CSS via its own framework; in DotCMS we must add
        // an explicit stylesheet reference.  Skip the prepend when a
        // DnnCssInclude directive already emitted a skin.css link above.
        if (!string.IsNullOrWhiteSpace(themeName) &&
            !html.Contains($"/application/themes/{themeName}/skin.css", StringComparison.OrdinalIgnoreCase))
        {
            string cssLink = $@"<link rel=""stylesheet"" href=""/application/themes/{themeName}/skin.css"" />";
            html = cssLink + "\n" + html;
        }

        // DNN also automatically loads a per-skin CSS file that matches the
        // skin filename (e.g. Home.css for Home.ascx).  Inject the link when
        // both a theme and skin name are known, and the file isn't already
        // referenced (skin.css is handled above, so skip when the names match).
        if (!string.IsNullOrWhiteSpace(themeName) &&
            !string.IsNullOrWhiteSpace(skinName) &&
            !string.Equals(skinName, "skin", StringComparison.OrdinalIgnoreCase))
        {
            string skinCssHref = $"/application/themes/{themeName}/{skinName}.css";
            if (!html.Contains(skinCssHref, StringComparison.OrdinalIgnoreCase))
            {
                string skinCssLink = $@"<link rel=""stylesheet"" href=""{skinCssHref}"" />";
                html = skinCssLink + "\n" + html;
            }
        }

        return html;
    }

    // ------------------------------------------------------------------
    // Theme static-asset helper
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads non-ASCX static files from <paramref name="themesZipPath"/>,
    /// builds the DotCMS folder hierarchy, and writes each file as a proper
    /// <c>FileAsset</c> contentlet so that DotCMS's push-publish importer
    /// creates the <c>/application/themes/</c> directory tree and places the
    /// files correctly.
    /// </summary>
    private static void WriteThemeFileAssets(
        TarWriter tar,
        string themesZipPath,
        string hostId,
        string? siteId,
        string? siteInode,
        string contentWorkDir,
        List<(string type, string id, string inode, string name, string site, string folder)> manifestEntries)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(themesZipPath);

        // Collect all non-ASCX files with their mapped DotCMS relative paths.
        // relPath is like "application/themes/Xcillion/Css/skin.css".
        var themeFiles = new List<(string relPath, string fileName, byte[] content)>();

        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/'))
                continue;
            if (entry.Name.EndsWith(".ascx", StringComparison.OrdinalIgnoreCase))
                continue;

            string entryPath = entry.FullName.Replace('\\', '/');
            string bundlePath = MapThemePath(entryPath);

            // Strip the "ROOT/" prefix to get the relative path.
            const string rootPrefix = "ROOT/";
            string relPath = bundlePath.StartsWith(rootPrefix, StringComparison.Ordinal)
                ? bundlePath[rootPrefix.Length..]
                : bundlePath;

            using var entryStream = entry.Open();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);

            themeFiles.Add((relPath, entry.Name, ms.ToArray()));
        }

        if (themeFiles.Count == 0)
            return;

        // Build the folder hierarchy.  Collect every unique directory path
        // (e.g. "application", "application/themes", "application/themes/Xcillion",
        //  "application/themes/Xcillion/Css") and assign each a stable UUID.
        var folderInodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relPath, _, _) in themeFiles)
        {
            string? dir = Path.GetDirectoryName(relPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir)) continue;

            string[] parts = dir.Split('/');
            for (int depth = 1; depth <= parts.Length; depth++)
            {
                string partialPath = string.Join("/", parts[..depth]);
                folderInodes.TryAdd(partialPath, Guid.NewGuid().ToString());
            }
        }

        // Write FolderWrapper XML for each directory level when a site is available.
        if (siteId is not null && siteInode is not null)
        {
            foreach (var (dirPath, folderInode) in folderInodes)
            {
                string folderName = dirPath.Split('/')[^1];
                string dotcmsPath = "/" + dirPath + "/";
                int lastSlash     = dirPath.LastIndexOf('/');
                string parentPath = lastSlash >= 0
                    ? "/" + dirPath[..lastSlash] + "/"
                    : "/";

                string folderXml = BuildFolderXml(
                    folderInode, folderName, dotcmsPath, parentPath, siteId, siteInode);
                // Place the .folder.xml entry in a nested directory
                // that mirrors the folder hierarchy so DotCMS's
                // FolderHandler (which sorts by file-path length)
                // processes parent folders before their children.
                string entryDir = parentPath == "/"
                    ? "ROOT"
                    : "ROOT/" + parentPath.Trim('/');
                WriteTextEntry(tar, $"{entryDir}/{folderInode}.folder.xml", folderXml);
                manifestEntries.Add(("folder", folderInode, folderInode, dotcmsPath, contentWorkDir, "/"));
            }
        }

        // Write each static file as a FileAsset contentlet.
        foreach (var (relPath, fileName, content) in themeFiles)
        {
            string identifier = Guid.NewGuid().ToString();
            string inode      = Guid.NewGuid().ToString();

            // Determine the containing folder.
            string? dir        = Path.GetDirectoryName(relPath)?.Replace('\\', '/');
            string  folderPath = string.IsNullOrEmpty(dir) ? "" : dir + "/";
            string  folderInode = !string.IsNullOrEmpty(dir) &&
                                  folderInodes.TryGetValue(dir, out string? foundInode)
                ? foundInode
                : "SYSTEM_FOLDER";

            // Write binary content.
            string assetPath = BuildAssetPath(inode, fileName);
            WriteBinaryEntry(tar, assetPath, content);

            // Write contentlet XML.
            string fileContentXml = BuildFileAssetXml(
                identifier, inode, fileName, assetPath,
                hostId, folderPath, folderInode);
            WriteTextEntry(
                tar,
                $"live/{contentWorkDir}/1/{identifier}.content.xml",
                fileContentXml);

            // Write workflow XML.
            string workflowXml = BuildContentWorkflowXml(identifier, fileName);
            WriteTextEntry(
                tar,
                $"live/{contentWorkDir}/1/{identifier}.contentworkflow.xml",
                workflowXml);

            manifestEntries.Add(("contentlet", identifier, inode,
                Truncate(fileName, MaxVarcharLength), contentWorkDir, "/"));
        }
    }

    private static string MapThemePath(string zipEntryPath)
    {
        // "_default/Skins/Xcillion/Css/skin.css"
        //   → "ROOT/application/themes/Xcillion/Css/skin.css"
        // "_default/Containers/Xcillion/…"
        //   → "ROOT/application/themes/Xcillion/Containers/…"
        //
        // The ROOT/ prefix causes DotCMS to write these files into the
        // /application/themes/ directory on the file system, which is where
        // DotCMS expects theme static assets to live.
        const string skinsPrefix      = "_default/Skins/";
        const string containersPrefix = "_default/Containers/";

        if (zipEntryPath.StartsWith(skinsPrefix, StringComparison.OrdinalIgnoreCase))
            return "ROOT/application/themes/" + zipEntryPath[skinsPrefix.Length..];

        if (zipEntryPath.StartsWith(containersPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Preserve container name but nest under a "Containers" sub-folder
            // to avoid conflicts with skin files sharing the same theme name.
            string rest = zipEntryPath[containersPrefix.Length..];
            int slash = rest.IndexOf('/');
            return slash < 0
                ? "ROOT/application/themes/" + rest
                : $"ROOT/application/themes/{rest[..slash]}/Containers/{rest[(slash + 1)..]}";
        }

        // Fallback: keep the original path under ROOT/application/themes/
        return "ROOT/application/themes/" + zipEntryPath;
    }

    // ------------------------------------------------------------------
    // Utility helpers
    // ------------------------------------------------------------------

    private static void WriteTextEntry(TarWriter tar, string path, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using var ms = new MemoryStream(bytes);
        var entry = new UstarTarEntry(TarEntryType.RegularFile, path)
        {
            DataStream = ms,
        };
        tar.WriteEntry(entry);
    }

    private static void WriteBinaryEntry(TarWriter tar, string path, byte[] content)
    {
        using var ms = new MemoryStream(content);
        var entry = new UstarTarEntry(TarEntryType.RegularFile, path)
        {
            DataStream = ms,
        };
        tar.WriteEntry(entry);
    }


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
    /// Converts a DNN portal/site name into a site name suitable for use as a
    /// DotCMS site identifier, preserving the original casing and replacing
    /// spaces (and other non-alphanumeric characters) with underscores.
    /// For example: <c>"My Website"</c> → <c>"My_Website"</c>.
    /// </summary>
    public static string SanitizeHostname(string siteName)
    {
        // Replace runs of non-alphanumeric characters (including spaces) with an underscore.
        string sanitized = Regex.Replace(siteName, @"[^A-Za-z0-9]+", "_");
        sanitized = sanitized.Trim('_');
        return string.IsNullOrEmpty(sanitized) ? "imported-site" : sanitized;
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

    /// <summary>
    /// Truncates <paramref name="value"/> to at most <paramref name="maxLength"/>
    /// characters.  This prevents "value too long for type character varying(255)"
    /// errors when dotCMS stores the field in a VARCHAR column.
    /// </summary>
    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
