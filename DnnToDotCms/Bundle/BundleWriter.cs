using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
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
        IReadOnlyList<DnnPortalFile>? portalFiles = null,
        IReadOnlyList<(string id, string inode, string name, string html, string themeName)>? prebuiltContainers = null,
        IReadOnlyList<(string id, string inode, string name, string html, string header, string themeName, IReadOnlyDictionary<string, int> paneUuidMap)>? prebuiltTemplates = null)
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
        var containerDefs = new List<(string id, string inode, string name, string html, string themeName)>();
        var templateDefs  = new List<(string id, string inode, string name, string html, string header, string themeName, IReadOnlyDictionary<string, int> paneUuidMap)>();

        // When pre-built container/template definitions are supplied (e.g.
        // from the --crawl code path with layout extraction) use them
        // directly, bypassing both the themes-zip and fallback paths.
        if (prebuiltContainers is not null && prebuiltContainers.Count > 0
            && prebuiltTemplates is not null && prebuiltTemplates.Count > 0)
        {
            containerDefs.AddRange(prebuiltContainers);
            templateDefs.AddRange(prebuiltTemplates);
        }
        else if (themesZipPath is not null && File.Exists(themesZipPath))
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

        // When no theme definitions were collected but we have both HTML
        // content and portal pages, create a minimal default container and
        // template so that BundleWriter can link content to pages via
        // multiTree — matching the bundle structure produced by the export
        // path.  This is the typical scenario for the --crawl code path
        // where no export_themes.zip is available.
        if (containerDefs.Count == 0 && templateDefs.Count == 0
            && htmlContents is not null && htmlContents.Count > 0
            && pages is not null && pages.Count > 0)
        {
            // $!{dotContent.body} is DotCMS Velocity syntax that renders the
            // "body" field of each contentlet placed in this container.  The
            // $! prefix suppresses null output when the field is empty.
            string defaultContainerCode = "$!{dotContent.body}";
            string containerId   = Guid.NewGuid().ToString();
            string containerInode = Guid.NewGuid().ToString();
            containerDefs.Add((containerId, containerInode, "Standard", defaultContainerCode, ""));

            // #parseContainer is a DotCMS Velocity directive that renders a
            // container slot in the template.  The first argument is the
            // container identifier, the second is a unique instance UUID.
            var paneMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["ContentPane"] = 1
            };
            string templateBody = $"#parseContainer('{containerId}', '1')";
            templateDefs.Add((Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                "Default", templateBody, "", "", paneMap));
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
            string id   = DeterministicId("ContentType:" + ct.Variable);
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
        foreach (var (id, inode, name, html, _) in containerDefs)
        {
            string xml = BuildContainerXml(id, inode, name, html, contentHostId,
                htmlContentTypeId ?? string.Empty);
            WriteTextEntry(tar, $"live/{contentWorkDir}/{id}.containers.container.xml", xml);
            manifestEntries.Add(("containers", id, inode, name, "", ""));
        }

        // --- templates are written later, after per-pane container resolution ---

        // --- HTML contentlets (from DNN HTML modules) ------------------------
        // Pre-generate identifiers so they can be referenced in page multiTree entries.
        // Build a lookup: tab UniqueId → list of (contentlet identifier, pane name).
        var tabContentMap = new Dictionary<string, List<(string contentletId, string paneName)>>(StringComparer.OrdinalIgnoreCase);

        // Collect per-pane container source from content items so templates
        // can use the correct container for each pane slot.
        // Key: paneName (case-insensitive) → ContainerSrc (most common value).
        var paneContainerVotes = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        if (htmlContents is not null && htmlContents.Count > 0
            && htmlContentTypeId is not null && htmlContentTypeVariable is not null)
        {
            // Prefer a container named "standard" as the default; fall back to the
            // first container if no "standard" container exists.
            string defaultContainerId = ResolveDefaultContainerId(containerDefs);

            foreach (DnnHtmlContent hc in htmlContents)
            {
                string identifier = Guid.NewGuid().ToString();
                string inode      = Guid.NewGuid().ToString();

                // Resolve the icon file path to a portal-root-relative URL.
                string imageUrl = string.Empty;
                if (!string.IsNullOrEmpty(hc.IconFile))
                    imageUrl = "/" + hc.IconFile.TrimStart('/');

                string contentXml = BuildContentXml(
                    identifier, inode, hc.Title, hc.HtmlBody,
                    contentHostId, htmlContentTypeId, htmlContentTypeVariable,
                    imageUrl);
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
                    if (!tabContentMap.TryGetValue(hc.TabUniqueId, out var items))
                    {
                        items = [];
                        tabContentMap[hc.TabUniqueId] = items;
                    }
                    items.Add((identifier, hc.PaneName));
                }

                // Track per-pane container votes for template resolution.
                if (!string.IsNullOrEmpty(hc.PaneName) && !string.IsNullOrEmpty(hc.ContainerSrc))
                {
                    if (!paneContainerVotes.TryGetValue(hc.PaneName, out var votes))
                    {
                        votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        paneContainerVotes[hc.PaneName] = votes;
                    }
                    votes.TryGetValue(hc.ContainerSrc, out int count);
                    votes[hc.ContainerSrc] = count + 1;
                }
            }
        }

        // Build per-pane container ID mapping from the collected votes.
        // For each pane, pick the most common container source and resolve it
        // to a DotCMS container identifier.
        var paneContainerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        {
            string defaultCtr = ResolveDefaultContainerId(containerDefs);
            foreach (var (paneName, votes) in paneContainerVotes)
            {
                string topSrc = votes.OrderByDescending(kv => kv.Value).First().Key;
                string resolved = ResolveContainerIdFromSrc(topSrc, containerDefs, defaultCtr);
                if (resolved != defaultCtr)
                    paneContainerIds[paneName] = resolved;
            }
        }

        // --- templates (from DNN skins) --------------------------------------
        // Written to live/ after per-pane container resolution so the correct
        // container IDs are baked into each #parseContainer directive.
        foreach (var (id, inode, name, html, header, themeName, paneMap) in templateDefs)
        {
            // Post-process the template HTML to replace the default container
            // with the per-pane container where applicable.
            string finalHtml = html;
            if (paneContainerIds.Count > 0 && paneMap.Count > 0)
            {
                string defaultCtr = ResolveDefaultContainerId(containerDefs);
                foreach (var (paneName, uuid) in paneMap)
                {
                    if (paneContainerIds.TryGetValue(paneName, out string? paneContainerId))
                    {
                        string oldDirective = $"#parseContainer('{defaultCtr}', '{uuid}')";
                        string newDirective = $"#parseContainer('{paneContainerId}', '{uuid}')";
                        finalHtml = finalHtml.Replace(oldDirective, newDirective);
                    }
                }
            }

            string xml = BuildTemplateXml(id, inode, name, finalHtml, contentHostId, themeName, header);
            WriteTextEntry(tar, $"live/{contentWorkDir}/{id}.template.template.xml", xml);
            manifestEntries.Add(("template", id, inode, name, "", ""));
        }

        // --- portal pages (from DNN tabs) ------------------------------------

        // Unified folder inode registry keyed by DotCMS folder path
        // (case-insensitive).  Shared across page folders, portal-file
        // folders, and theme folders to prevent duplicate identifier
        // entries that would violate the
        // identifier_parent_path_asset_name_host_inode_key constraint.
        var unifiedFolderInodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Track DotCMS folder paths for which folder XML has already
        // been written so we never emit two entries with the same
        // (parentPath, assetName, hostId) tuple.
        var writtenFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pre-register portal-file folder paths in the unified registry
        // so that page processing can detect collisions with file folders
        // (e.g. a top-level page "images" colliding with folder "images/").
        if (portalFiles is not null)
        {
            foreach (DnnPortalFile pf in portalFiles)
            {
                if (string.IsNullOrEmpty(pf.FolderPath)) continue;
                string trimmed = pf.FolderPath.TrimEnd('/');
                string[] parts = trimmed.Split('/');
                for (int depth = 1; depth <= parts.Length; depth++)
                {
                    string partialPath = string.Join("/", parts[..depth]) + "/";
                    string dotcmsKey = "/" + partialPath;
                    if (!dotcmsKey.EndsWith('/')) dotcmsKey += '/';
                    if (!unifiedFolderInodes.ContainsKey(dotcmsKey))
                        unifiedFolderInodes[dotcmsKey] = Guid.NewGuid().ToString();
                }
            }
        }

        if (pages is not null && pages.Count > 0)
        {
            // Build a lookup from skin name → template ID for matching DNN pages
            // to the templates that were converted from DNN skins.
            var skinToTemplateId = BuildSkinToTemplateMap(templateDefs);

            // Build a lookup from template ID → pane-UUID mapping so each page
            // can resolve its content modules to the correct container slots.
            var templatePaneMaps = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (tId, _, _, _, _, _, paneMap) in templateDefs)
            {
                if (paneMap.Count > 0)
                    templatePaneMaps.TryAdd(tId, paneMap);
            }

            // Prefer a container named "standard" as the default; fall back to the
            // first container if no "standard" container exists.
            string defaultContainerId = ResolveDefaultContainerId(containerDefs);

            // Build the folder hierarchy for child pages (Level > 0).
            // DotCMS pages live inside folders; top-level pages use SYSTEM_FOLDER
            // (the site root "/"), while child pages require explicit folders.
            // DNN TabPath like "//Personal//CheckingAccounts" maps to a DotCMS
            // folder "/personal/" with the page URL "checking-accounts".
            var pageFolderInodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DnnPortalPage page in pages)
            {
                // Level-0 (top-level) pages use SYSTEM_FOLDER and parentPath="/"
                // so they don't need explicit folder entries.  Only child pages
                // (Level >= 1) require folder creation.
                if (page.Level < 1) continue;
                if (page.Name.Equals("Admin", StringComparison.OrdinalIgnoreCase)) continue;

                // Parse TabPath to extract parent folder segments.
                // "//Personal//CheckingAccounts" → ["Personal"]
                string[] pathSegments = page.TabPath
                    .Split("//", StringSplitOptions.RemoveEmptyEntries);
                if (pathSegments.Length < 2) continue;

                // Build each parent folder level (all segments except the last, which is the page itself).
                for (int depth = 1; depth < pathSegments.Length; depth++)
                {
                    string folderSlug = string.Join("/",
                        pathSegments[..depth].Select(s => PageNameToUrl(s)));
                    string dotcmsKey = "/" + folderSlug + "/";
                    if (!unifiedFolderInodes.TryGetValue(dotcmsKey, out string? existingInode))
                    {
                        existingInode = Guid.NewGuid().ToString();
                        unifiedFolderInodes[dotcmsKey] = existingInode;
                    }
                    pageFolderInodes.TryAdd(folderSlug, existingInode);
                }
            }

            // Write FolderWrapper XML for each page folder so DotCMS creates
            // the folder structure before importing child pages.
            if (siteId is not null && siteInode is not null)
            {
                foreach (var (folderSlug, folderInode) in pageFolderInodes)
                {
                    string folderName   = folderSlug.Split('/')[^1];
                    string dotcmsPath   = "/" + folderSlug + "/";
                    int lastSlash       = folderSlug.LastIndexOf('/');
                    string pageFolderParent = lastSlash >= 0
                        ? "/" + folderSlug[..lastSlash] + "/"
                        : "/";

                    string folderXml = BuildFolderXml(
                        folderInode, folderName, dotcmsPath, pageFolderParent,
                        siteId, siteInode);
                    string entryDir = pageFolderParent == "/"
                        ? "ROOT"
                        : "ROOT/" + pageFolderParent.Trim('/');
                    WriteTextEntry(tar, $"{entryDir}/{folderInode}.folder.xml", folderXml);
                    manifestEntries.Add(("folder", folderInode, folderInode, dotcmsPath, contentWorkDir, "/"));
                    writtenFolderPaths.Add(dotcmsPath);
                }
            }

            foreach (DnnPortalPage page in pages)
            {
                // Skip admin pages and deleted pages.
                if (page.Name.Equals("Admin", StringComparison.OrdinalIgnoreCase)) continue;
                // Skip child pages under Admin.
                if (page.TabPath.Contains("//Admin//", StringComparison.OrdinalIgnoreCase)) continue;

                string identifier = Guid.NewGuid().ToString();
                string inode      = Guid.NewGuid().ToString();
                string url        = page.Name.Equals("Home", StringComparison.OrdinalIgnoreCase)
                    ? "index"
                    : PageNameToUrl(page.Name);
                string templateId = ResolveTemplateId(page.SkinSrc, skinToTemplateId);

                // Resolve the HTML contentlet identifiers belonging to this page.
                tabContentMap.TryGetValue(page.UniqueId, out var pageContentItems);

                // Resolve the pane-UUID mapping for the page's template.
                templatePaneMaps.TryGetValue(templateId, out var paneUuidMap);

                // Resolve the folder and parentPath for child pages.
                string pageFolderInode = "SYSTEM_FOLDER";
                string pageParentPath  = "/";
                if (page.Level > 0)
                {
                    string[] pathSegments = page.TabPath
                        .Split("//", StringSplitOptions.RemoveEmptyEntries);
                    if (pathSegments.Length >= 2)
                    {
                        string folderSlug = string.Join("/",
                            pathSegments[..^1].Select(s => PageNameToUrl(s)));
                        if (pageFolderInodes.TryGetValue(folderSlug, out string? fi))
                            pageFolderInode = fi;
                        pageParentPath = "/" + folderSlug + "/";
                    }
                }

                // When a top-level page's URL would collide with an existing
                // folder (from child pages or portal files), move the page
                // inside the folder as "index" to avoid a duplicate
                // identifier_parent_path_asset_name_host_inode_key violation.
                if (page.Level == 0
                    && unifiedFolderInodes.TryGetValue("/" + url + "/", out string? conflictFolderInode))
                {
                    pageFolderInode = conflictFolderInode;
                    pageParentPath  = "/" + url + "/";
                    url             = "index";
                }

                string pageXml = BuildPageXml(
                    identifier, inode, page.Title, url,
                    contentHostId, templateId,
                    defaultContainerId, pageContentItems ?? [], paneUuidMap,
                    paneContainerIds, page.IsVisible, page.TabOrder,
                    pageFolderInode, pageParentPath);
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

        // --- theme static files as FileAsset contentlets (optional) ----------
        // Theme static files (CSS, JS, images, fonts) from export_themes.zip
        // must be written as proper DotCMS FileAsset contentlets with folder
        // hierarchy XML so that DotCMS's push-publish importer creates the
        // /application/themes/ directory structure on the target server.
        // Raw files under ROOT/ are NOT automatically imported; they require
        // full contentlet metadata (content XML + workflow XML + binary asset)
        // plus FolderWrapper XML entries for each directory level.
        //
        // This runs BEFORE portal files so that per-skin CSS files can be
        // resolved from export_files.zip portal files; consumed portal files
        // are tracked and excluded from the site-root write below.
        var consumedPortalFileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (themesZipPath is not null && File.Exists(themesZipPath))
        {
            try
            {
                WriteThemeFileAssets(
                    tar, themesZipPath, contentHostId,
                    siteId, siteInode, contentWorkDir, manifestEntries,
                    templateDefs, portalFiles, consumedPortalFileIds,
                    unifiedFolderInodes, writtenFolderPaths);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Console.Error.WriteLine(
                    $"Warning: Could not read theme assets from '{themesZipPath}': {ex.Message}");
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
                    string dotcmsKey = "/" + partialPath;
                    if (!dotcmsKey.EndsWith('/')) dotcmsKey += '/';
                    // Reuse an existing folder inode from the unified
                    // registry (e.g. created by page-folder building)
                    // to avoid duplicate identifier entries.
                    if (!unifiedFolderInodes.TryGetValue(dotcmsKey, out string? existingInode))
                    {
                        existingInode = Guid.NewGuid().ToString();
                        unifiedFolderInodes[dotcmsKey] = existingInode;
                    }
                    folderInodes.TryAdd(partialPath, existingInode);
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

                    // Skip folders already written (e.g. by page-folder
                    // building) to avoid duplicate identifier entries.
                    if (!writtenFolderPaths.Add(dotcmsPath))
                        continue;

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
                // Skip files that were already placed in the theme folder
                // by WriteThemeFileAssets (e.g. per-skin CSS resolved from
                // portal files instead of from the themes zip).
                if (consumedPortalFileIds.Contains(pf.UniqueId))
                    continue;

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

                // Portal files are also written as static web resources under
                // ROOT/{folderPath}/{fileName} so that URL references in
                // migrated HTML content resolve correctly.
                // DNN HTML uses {{PortalRoot}} which is replaced with "/",
                // so the file path in the bundle mirrors the DNN folder layout:
                //   {{PortalRoot}}Images/x  →  /Images/x
                //   {{PortalRoot}}Other/x   →  /Other/x
                //   {{PortalRoot}}file.css  →  /file.css
                //
                // Theme-related folders (Containers/, Skins/) are already
                // processed from export_themes.zip by WriteThemeFileAssets and
                // are not referenced via {{PortalRoot}} tokens, so they are
                // excluded from static-resource writing.
                string normalizedFolderPath = pf.FolderPath;
                if (normalizedFolderPath.Length > 0 && !normalizedFolderPath.EndsWith('/'))
                    normalizedFolderPath += '/';

                if (normalizedFolderPath.StartsWith("Containers/", StringComparison.OrdinalIgnoreCase) ||
                    normalizedFolderPath.StartsWith("Skins/", StringComparison.OrdinalIgnoreCase))
                {
                    // Theme-related folder — skip static resource writing.
                }
                else
                {
                    // All portal files (including Images/) are written to
                    // ROOT/{folderPath}/{fileName}, matching the uniform
                    // {{PortalRoot}} → "/" replacement.
                    WriteBinaryEntry(tar, "ROOT/" + normalizedFolderPath + pf.FileName, pf.Content);
                }
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
                Id            = DeterministicId($"Field:{contentTypeId}:{f.Variable}"),
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
        Id            = DeterministicId($"Field:{contentTypeId}:{variable}"),
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
        string themeName = "", string headerContent = "")
    {
        string now      = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string xmlBody  = System.Security.SecurityElement.Escape(body) ?? string.Empty;
        string xmlTitle = System.Security.SecurityElement.Escape(Truncate(title, MaxVarcharLength)) ?? string.Empty;
        // The header field is rendered by DotCMS inside the HTML <head> section.
        // CSS <link> tags extracted from DNN skins belong here so browsers
        // receive them in <head> — matching DNN's behaviour where the client-
        // resource framework registered stylesheets in <head>.
        string xmlHeader = string.IsNullOrWhiteSpace(headerContent)
            ? "null"
            : (System.Security.SecurityElement.Escape(headerContent) ?? "null");
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
                <header>{xmlHeader}</header>
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
    /// Wraps a key-value pair as an XStream map <c>&lt;entry&gt;</c> element.
    /// DotCMS (Java 11+) uses XStream's <c>MapConverter</c> for
    /// <c>ConcurrentHashMap</c>, which serialises each entry as
    /// <c>&lt;entry&gt;&lt;string&gt;key&lt;/string&gt;&lt;…&gt;value&lt;/…&gt;&lt;/entry&gt;</c>.
    /// The old Java 7/8 <c>serialization="custom"</c> format with segments is
    /// <b>not</b> deserializable on Java 11+ and causes an
    /// <c>IndexOutOfBoundsException</c> inside XStream's <c>MapConverter</c>.
    /// </summary>
    private static string E(string key, string valueElement)
        => $"<entry><string>{key}</string>{valueElement}</entry>";

    private static string EStr(string key, string value)
        => E(key, $"<string>{value}</string>");

    private static string ELong(string key, long value)
        => E(key, $"<long>{value}</long>");

    private static string EBool(string key, bool value)
        => E(key, $"<boolean>{(value ? "true" : "false")}</boolean>");

    private static string ETimestamp(string key, string ts)
        => E(key, $"<sql-timestamp>{ts}</sql-timestamp>");

    private static string EFile(string key, string path)
        => E(key, $"<file>{path}</file>");

    private static string BuildHostXml(string hostId, string hostInode, string hostname)
    {
        string now         = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string xmlHostname = System.Security.SecurityElement.Escape(hostname) ?? string.Empty;

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
                <map class="java.util.concurrent.ConcurrentHashMap">
                  {EStr("type", "host")}
                  {EStr("inode", hostInode)}
                  {EStr("hostname", xmlHostname)}
                  {EStr("hostName", xmlHostname)}
                  {EStr("__DOTNAME__", xmlHostname)}
                  {EStr("host", "SYSTEM_HOST")}
                  {EStr("stInode", HostContentTypeInode)}
                  {EStr("owner", "dotcms.org.1")}
                  {EStr("identifier", hostId)}
                  {ELong("languageId", 1)}
                  {EBool("runDashboard", false)}
                  {EBool("isSystemHost", false)}
                  {EBool("isDefault", false)}
                  {EStr("folder", "SYSTEM_FOLDER")}
                  {EStr("tagStorage", "SYSTEM_HOST")}
                  {ELong("sortOrder", 0)}
                  {EStr("modUser", "dotcms.org.1")}
                  {EBool("open", true)}
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
              <tags class="java.util.ArrayList"/>
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
        string contentTypeVariable,
        string imageUrl = "")
    {
        string now        = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string xmlTitle   = System.Security.SecurityElement.Escape(Truncate(title, MaxVarcharLength))   ?? string.Empty;
        string xmlBody    = System.Security.SecurityElement.Escape(htmlBody) ?? string.Empty;
        string xmlImage   = System.Security.SecurityElement.Escape(imageUrl) ?? string.Empty;

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
                <map class="java.util.concurrent.ConcurrentHashMap">
                  {ETimestamp("modDate", now)}
                  {EStr("inode", inode)}
                  {EStr("host", hostId)}
                  {EStr("stInode", contentTypeId)}
                  {EStr("title", xmlTitle)}
                  {EStr("body", xmlBody)}
                  {EStr("image", xmlImage)}
                  {EStr("owner", "dotcms.org.1")}
                  {EStr("identifier", identifier)}
                  {ELong("languageId", 1)}
                  {EStr("folder", "SYSTEM_FOLDER")}
                  {ELong("sortOrder", 0)}
                  {EStr("modUser", "dotcms.org.1")}
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
              <tags class="java.util.ArrayList"/>
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
              <history class="java.util.ArrayList"/>
              <comments class="java.util.ArrayList"/>
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
        IReadOnlyList<(string contentletId, string paneName)>? contentItems = null,
        IReadOnlyDictionary<string, int>? paneUuidMap = null,
        IReadOnlyDictionary<string, string>? paneContainerIds = null,
        bool showOnMenu = true,
        int sortOrder = 0,
        string folderInode = "SYSTEM_FOLDER",
        string parentPath = "/")
    {
        string now      = DateTime.UtcNow.ToString(XmlTimestampFormat);
        string xmlTitle = System.Security.SecurityElement.Escape(Truncate(title, MaxVarcharLength)) ?? string.Empty;
        string xmlUrl   = System.Security.SecurityElement.Escape(url) ?? string.Empty;
        string xmlParentPath = System.Security.SecurityElement.Escape(parentPath) ?? "/";
        string xmlFolderInode = System.Security.SecurityElement.Escape(folderInode) ?? "SYSTEM_FOLDER";

        // Build multiTree entries linking this page to its contentlets via the container.
        string multiTreeContent = BuildMultiTreeXml(identifier, containerId,
            contentItems ?? [], paneUuidMap, paneContainerIds);
        string multiTreeElement = string.IsNullOrEmpty(multiTreeContent)
            ? "<multiTree/>"
            : $"<multiTree>{multiTreeContent}</multiTree>";

        string showOnMenuStr = showOnMenu ? "true" : "false";

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
                <map class="java.util.concurrent.ConcurrentHashMap">
                  {ETimestamp("modDate", now)}
                  {EStr("cachettl", "0")}
                  {EStr("inode", inode)}
                  {EStr("host", hostId)}
                  {EStr("stInode", HtmlPageAssetContentTypeId)}
                  {EStr("title", xmlTitle)}
                  {EStr("friendlyName", xmlTitle)}
                  {E("showOnMenu", $"<boolean>{showOnMenuStr}</boolean>")}
                  {EStr("template", templateId)}
                  {EStr("url", xmlUrl)}
                  {EStr("owner", "dotcms.org.1")}
                  {EStr("identifier", identifier)}
                  {ELong("languageId", 1)}
                  {EStr("folder", xmlFolderInode)}
                  {ELong("sortOrder", sortOrder)}
                  {EStr("modUser", "dotcms.org.1")}
                </map>
                <lowIndexPriority>false</lowIndexPriority>
                <variantId>DEFAULT</variantId>
              </content>
              <id>
                <id>{identifier}</id>
                <assetName>{xmlUrl}</assetName>
                <assetType>contentlet</assetType>
                <parentPath>{xmlParentPath}</parentPath>
                <hostId>{hostId}</hostId>
                <owner>dotcms.org.1</owner>
                <createDate class="sql-timestamp">{now}</createDate>
                <assetSubType>htmlpageasset</assetSubType>
              </id>
              {multiTreeElement}
              <tree/>
              <categories/>
              <tags class="java.util.ArrayList"/>
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
    /// content items placed in <paramref name="defaultContainerId"/>.
    /// <para>
    /// When <paramref name="paneUuidMap"/> is provided, each content item's
    /// pane name is resolved to the matching <c>#parseContainer</c> UUID slot
    /// in the template, ensuring that footer modules (or any pane-specific
    /// content) appear in the correct template region.  Multiple items sharing
    /// the same pane are distinguished by <c>tree_order</c>.
    /// </para>
    /// <para>
    /// When <paramref name="paneContainerIds"/> is provided, each content
    /// item's pane name is resolved to the correct DotCMS container identifier.
    /// This is critical for panes that use a non-default container (e.g.
    /// <c>hpcard</c> or <c>card</c> containers) — without this, content items
    /// in those panes would reference the wrong container and DotCMS would not
    /// render them.
    /// </para>
    /// Returns an empty string when no container or content items are given.
    /// </summary>
    private static string BuildMultiTreeXml(
        string pageId,
        string defaultContainerId,
        IReadOnlyList<(string contentletId, string paneName)> contentItems,
        IReadOnlyDictionary<string, int>? paneUuidMap = null,
        IReadOnlyDictionary<string, string>? paneContainerIds = null)
    {
        if (string.IsNullOrEmpty(defaultContainerId) || contentItems.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        // When no pane mapping is available, fall back to sequential
        // relation_type assignment (the original behaviour).
        if (paneUuidMap is null || paneUuidMap.Count == 0)
        {
            for (int i = 0; i < contentItems.Count; i++)
            {
                string relationType = (i + 1).ToString();
                // Resolve per-pane container ID when available.
                string containerId = ResolveContentContainerId(
                    contentItems[i].paneName, paneContainerIds, defaultContainerId);
                AppendMultiTreeEntry(sb, pageId, containerId,
                    contentItems[i].contentletId, relationType, i);
            }
        }
        else
        {
            // Group content items by their resolved pane UUID so multiple
            // items in the same pane get sequential tree_order values.
            // Items whose pane name is unknown fall back to the first UUID.
            int fallbackUuid = 1;
            var paneOrders = new Dictionary<int, int>(); // uuid → next tree_order
            int globalOrder = 0;

            foreach (var (contentletId, paneName) in contentItems)
            {
                int uuid;
                if (!string.IsNullOrEmpty(paneName) && paneUuidMap.TryGetValue(paneName, out int mapped))
                    uuid = mapped;
                else
                    uuid = fallbackUuid;

                if (!paneOrders.TryGetValue(uuid, out int order))
                    order = 0;
                paneOrders[uuid] = order + 1;

                // Resolve per-pane container ID when available.
                string containerId = ResolveContentContainerId(
                    paneName, paneContainerIds, defaultContainerId);

                AppendMultiTreeEntry(sb, pageId, containerId,
                    contentletId, uuid.ToString(), globalOrder++);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Resolves the container ID for a content item based on its pane name.
    /// Returns the pane-specific container when available; otherwise falls
    /// back to <paramref name="defaultContainerId"/>.
    /// </summary>
    private static string ResolveContentContainerId(
        string paneName,
        IReadOnlyDictionary<string, string>? paneContainerIds,
        string defaultContainerId)
    {
        if (paneContainerIds is not null
            && !string.IsNullOrEmpty(paneName)
            && paneContainerIds.TryGetValue(paneName, out string? paneContainerId))
            return paneContainerId;
        return defaultContainerId;
    }

    /// <summary>Appends a single multiTree &lt;map&gt; entry to <paramref name="sb"/>.</summary>
    private static void AppendMultiTreeEntry(
        StringBuilder sb, string pageId, string containerId,
        string contentletId, string relationType, int treeOrder)
    {
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
                      <string>{contentletId}</string>
                    </entry>
                    <entry>
                      <string>relation_type</string>
                      <string>{relationType}</string>
                    </entry>
                    <entry>
                      <string>tree_order</string>
                      <int>{treeOrder}</int>
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
                <map class="java.util.concurrent.ConcurrentHashMap">
                  {ETimestamp("modDate", now)}
                  {EStr("fileName", xmlFileName)}
                  {EStr("title", xmlFileName)}
                  {EStr("inode", inode)}
                  {EStr("host", hostId)}
                  {EStr("stInode", FileAssetContentTypeId)}
                  {EStr("owner", "dotcms.org.1")}
                  {EStr("identifier", identifier)}
                  {ELong("languageId", 1)}
                  {EFile("fileAsset", storagePath)}
                  {EStr("folder", folderInode)}
                  {ELong("sortOrder", 0)}
                  {EStr("modUser", "dotcms.org.1")}
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
              <tags class="java.util.ArrayList"/>
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
                <map class="java.util.concurrent.ConcurrentHashMap">
                  {EStr("type", "host")}
                  {EStr("identifier", hostId)}
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
        IReadOnlyList<(string id, string inode, string name, string html, string header, string themeName, IReadOnlyDictionary<string, int> paneUuidMap)> templateDefs)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, _, name, _, _, themeName, _) in templateDefs)
        {
            // Primary key: "ThemeName/SkinName" – always unique across themes.
            if (!string.IsNullOrEmpty(themeName))
                map.TryAdd($"{themeName}/{name}", id);

            // Fallback key: bare skin name – kept only when there is no
            // collision across themes so legacy callers still work.
            map.TryAdd(name, id);
        }
        return map;
    }

    /// <summary>
    /// Resolves the DotCMS template ID for a DNN page based on its
    /// <paramref name="skinSrc"/> (e.g. <c>[G]Skins/Xcillion/Home.ascx</c>).
    /// The method first tries a theme-qualified lookup
    /// (<c>ThemeName/SkinName</c>) to disambiguate skins with identical file
    /// names across different themes, then falls back to a bare skin-name
    /// lookup, and finally to the first available template.
    /// </summary>
    private static string ResolveTemplateId(
        string skinSrc,
        Dictionary<string, string> skinToTemplateId)
    {
        if (!string.IsNullOrWhiteSpace(skinSrc))
        {
            // Extract both theme folder and skin file name from paths like
            // "[G]Skins/FidelityBankTexas/Home.ascx" or
            // "[L]Skins/Cavalier/Inner.ascx".
            string skinName = Path.GetFileNameWithoutExtension(skinSrc);
            string? themeName = ExtractThemeNameFromSkinSrc(skinSrc);

            // Prefer the theme-qualified key so that two themes with
            // identically named skins resolve to the correct template.
            if (themeName is not null
                && skinToTemplateId.TryGetValue($"{themeName}/{skinName}", out string? qualifiedId))
                return qualifiedId;

            // Fall back to bare skin name (works when there is no collision).
            if (skinToTemplateId.TryGetValue(skinName, out string? id))
                return id;
        }
        // Fall back to the first available template.
        foreach (string id in skinToTemplateId.Values)
            return id;
        return string.Empty;
    }

    /// <summary>
    /// Extracts the DNN theme (skin package) folder name from a
    /// <c>SkinSrc</c> value such as <c>[G]Skins/FidelityBankTexas/Home.ascx</c>.
    /// Returns <c>null</c> when the path doesn't match the expected pattern.
    /// </summary>
    internal static string? ExtractThemeNameFromSkinSrc(string skinSrc)
    {
        // Normalise to forward slashes and locate the "Skins/" segment.
        string normalised = skinSrc.Replace('\\', '/');
        int idx = normalised.IndexOf("Skins/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        // After "Skins/" we expect "ThemeName/SkinFile.ascx".
        string afterSkins = normalised[(idx + "Skins/".Length)..];
        int slash = afterSkins.IndexOf('/');
        if (slash <= 0) return null;

        return afterSkins[..slash];
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
        List<(string id, string inode, string name, string html, string themeName)> containerDefs,
        List<(string id, string inode, string name, string html, string header, string themeName, IReadOnlyDictionary<string, int> paneUuidMap)> templateDefs)
    {
        using var zip = ZipFile.OpenRead(themesZipPath);

        // First pass: collect containers so their IDs are available for template conversion.
        // Supports both _default/Containers/{Theme}/File.ascx and
        // {Package}/containers/{Theme}/File.ascx path formats.
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!entry.Name.EndsWith(".ascx", StringComparison.OrdinalIgnoreCase))
                continue;

            string entryPath = entry.FullName.Replace('\\', '/');

            // Locate the "/containers/" segment (case-insensitive) to handle
            // both _default/Containers/ and {Package}/containers/ layouts.
            int containersIdx = entryPath.IndexOf("/containers/", StringComparison.OrdinalIgnoreCase);
            if (containersIdx < 0) continue;

            // Only process ThemeName/ContainerName.ascx (exactly one slash after containers/)
            string rest = entryPath[(containersIdx + "/containers/".Length)..];
            if (rest.Count(c => c == '/') != 1) continue;

            string name = Path.GetFileNameWithoutExtension(entry.Name);
            // Extract the container's theme name (directory between containers/ and the file).
            int cSlash = rest.IndexOf('/');
            string containerTheme = cSlash >= 0 ? rest[..cSlash] : string.Empty;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            string html = ConvertAscxToContainerHtml(reader.ReadToEnd());
            containerDefs.Add((Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), name, html, containerTheme));
        }

        // Prefer a container named "standard" as the default; fall back to the
        // first container if no "standard" container exists.
        string firstContainerId = ResolveDefaultContainerId(containerDefs);

        // Build a set of available non-ASCX theme file paths so that
        // ConvertAscxToTemplateHtml only injects CSS link tags for files
        // that actually exist in the export (DNN auto-loads per-skin CSS
        // only when the file is present on disk).
        var availableThemeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/'))
                continue;
            if (entry.Name.EndsWith(".ascx", StringComparison.OrdinalIgnoreCase))
                continue;

            string entryPath = entry.FullName.Replace('\\', '/');
            string bundlePath = MapThemePath(entryPath);
            const string rootPrefix = "ROOT/";
            string relPath = bundlePath.StartsWith(rootPrefix, StringComparison.Ordinal)
                ? bundlePath[rootPrefix.Length..]
                : bundlePath;
            availableThemeFiles.Add(relPath);
        }

        // Second pass: collect templates, wiring pane divs to #parseContainer.
        // Supports both _default/Skins/{Theme}/File.ascx and
        // {Package}/skins/{Theme}/File.ascx path formats.
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!entry.Name.EndsWith(".ascx", StringComparison.OrdinalIgnoreCase))
                continue;

            string entryPath = entry.FullName.Replace('\\', '/');

            // Locate the "/skins/" segment (case-insensitive) to handle
            // both _default/Skins/ and {Package}/skins/ layouts.
            int skinsIdx = entryPath.IndexOf("/skins/", StringComparison.OrdinalIgnoreCase);
            if (skinsIdx < 0) continue;

            // Only process ThemeName/SkinName.ascx (exactly one slash after skins/)
            string rest = entryPath[(skinsIdx + "/skins/".Length)..];
            if (rest.Count(c => c == '/') != 1) continue;

            // Extract the theme name (the directory between skins/ and SkinName.ascx).
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

                // Resolve <%@ Register TagPrefix="..." Src="..." %> user
                // controls by inlining their source ASCX content.
                ascx = ResolveRegisteredControls(ascx, skinDir, zip);
            }

            var (html, header, paneUuidMap) = ConvertAscxToTemplateHtml(ascx, firstContainerId, themeName, name, availableThemeFiles);
            templateDefs.Add((Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), name, html, header, themeName, paneUuidMap));
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

    /// <summary>
    /// Resolves ASP.NET user control references registered via
    /// <c>&lt;%@ Register TagPrefix="..." TagName="..." Src="..." %&gt;</c>
    /// directives by reading the referenced ASCX file from
    /// <paramref name="zip"/> and inlining its content.  Only controls
    /// whose <c>Src</c> is a relative path (i.e. not starting with <c>~</c>)
    /// are resolved – those point to files within the skin package rather
    /// than DNN system controls.
    /// </summary>
    public static string ResolveRegisteredControls(
        string ascx, string skinDir, ZipArchive zip)
    {
        // Collect <%@ Register TagPrefix="..." TagName="..." Src="..." %> entries
        // that reference a local (non-system) file.
        var registrations = new List<(string Prefix, string Name, string Src)>();
        foreach (Match m in RegisterDirectiveRegex.Matches(ascx))
        {
            string block = m.Value;
            Match prefix = TagPrefixAttrRegex.Match(block);
            Match name   = TagNameAttrRegex.Match(block);
            Match src    = SrcAttrRegex.Match(block);
            if (prefix.Success && name.Success && src.Success)
            {
                string srcVal = src.Groups[1].Value;
                // Only resolve local files; ~/Admin/... etc. are DNN system controls.
                if (!srcVal.StartsWith("~", StringComparison.Ordinal))
                    registrations.Add((prefix.Groups[1].Value, name.Groups[1].Value, srcVal));
            }
        }

        // For each registration, inline the file content in place of the control tag.
        foreach (var (prefix, name, src) in registrations)
        {
            string escapedPrefix = Regex.Escape(prefix);
            string escapedName   = Regex.Escape(name);

            // Match <prefix:name ...>...</prefix:name> (open/close pair) or
            // <prefix:name .../> (self-closing).  Open/close first so we
            // don't leave orphan closing tags.
            var tagRegex = new Regex(
                $@"<{escapedPrefix}:{escapedName}\b[^>]*>[\s\S]*?</{escapedPrefix}:{escapedName}\s*>" +
                $@"|<{escapedPrefix}:{escapedName}\b[^>]*/?>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Resolve the source file path relative to the skin directory.
            string relPath  = src.Replace('\\', '/');
            string fullPath = skinDir + "/" + relPath;
            ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e =>
                string.Equals(
                    e.FullName.Replace('\\', '/'),
                    fullPath,
                    StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                Console.Error.WriteLine(
                    $"Warning: Registered control source not found in themes zip: '{fullPath}'");
                // Remove the unresolvable control tag to avoid leaving server markup.
                ascx = tagRegex.Replace(ascx, string.Empty);
                continue;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            string content = reader.ReadToEnd();

            // Strip <%@ ... %> directives from the inlined content so they
            // don't interfere with the parent ASCX processing.
            content = DirectiveRegex.Replace(content, string.Empty);

            ascx = tagRegex.Replace(ascx, content);
        }

        return ascx;
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
         @"<nav class=""dnn-nav""><!-- Navigation: configure in DotCMS --></nav>"),
        (new(@"<dnn:NAV\s[^>]*/?>",         RegexOptions.IgnoreCase | RegexOptions.Singleline),
         @"<nav class=""dnn-nav""><!-- Navigation: configure in DotCMS --></nav>"),
        (new(@"<dnn:USER\s[^>]*/?>",        RegexOptions.IgnoreCase | RegexOptions.Singleline),
         @"<span class=""dnn-user"">$!{user.firstName} $!{user.lastName}</span>"),
        (new(@"<dnn:LOGIN\s[^>]*/?>",       RegexOptions.IgnoreCase | RegexOptions.Singleline),
         @"<span class=""dnn-login"">#if($!{user} && $!{user.userId} != ""anonymous"") <a href=""/dotAdmin"">My Account</a> #else <a href=""/dotAdmin/login"">Login</a> #end</span>"),
        (new(@"<dnn:USERANDLOGIN\s[^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
         @"<span class=""dnn-login"">#if($!{user} && $!{user.userId} != ""anonymous"") <a href=""/dotAdmin"">$!{user.firstName}</a> | <a href=""/api/v1/logout"">Logout</a> #else <a href=""/dotAdmin/login"">Login</a> #end</span>"),
        (new(@"<dnn:SEARCH\s[^>]*/?>",      RegexOptions.IgnoreCase | RegexOptions.Singleline),
         @"<form class=""dnn-search"" action=""/search"" method=""get""><input type=""text"" name=""q"" placeholder=""Search..."" aria-label=""Search"" /><button type=""submit"">Search</button></form>"),
        (new(@"<dnn:COPYRIGHT\s[^>]*/?>",   RegexOptions.IgnoreCase | RegexOptions.Singleline),
         "<span class=\"copyright\">Copyright</span>"),
        (new(@"<dnn:BREADCRUMB\s[^>]*/?>",  RegexOptions.IgnoreCase | RegexOptions.Singleline),
         @"<nav class=""dnn-breadcrumb"" aria-label=""Breadcrumb""><!-- Breadcrumb: configure in DotCMS --></nav>"),
        (new(@"<dnn:CURRENTDATE\s[^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
         @"<span class=""dnn-currentdate"">$date.format('MMMM d, yyyy', $date.date)</span>"),
        (new(@"<dnn:LANGUAGE\s[^>]*/?>",    RegexOptions.IgnoreCase | RegexOptions.Singleline),
         @"<span class=""dnn-language"">$!{language.languageCode}</span>"),
        (new(@"<dnn:TERMS\s[^>]*/?>",       RegexOptions.IgnoreCase | RegexOptions.Singleline),
         "<a href=\"/terms-of-use\">Terms of Use</a>"),
        (new(@"<dnn:PRIVACY\s[^>]*/?>",     RegexOptions.IgnoreCase | RegexOptions.Singleline),
         "<a href=\"/privacy\">Privacy</a>"),
        (new(@"<dnn:LINKS\s[^>]*/?>",       RegexOptions.IgnoreCase | RegexOptions.Singleline),
         string.Empty),
        (new(@"<dnn:TEXT\s[^>]*/?>",         RegexOptions.IgnoreCase | RegexOptions.Singleline),
         string.Empty),
        // Controls to remove entirely (handled by DotCMS)
        (new(@"<dnn:STYLES\s[^>]*/?>",      RegexOptions.IgnoreCase | RegexOptions.Singleline),
         string.Empty),
        (new(@"<dnn:JQUERY\s[^>]*/?>",      RegexOptions.IgnoreCase | RegexOptions.Singleline),
         string.Empty),
        (new(@"<dnn:META\s[^>]*/?>",        RegexOptions.IgnoreCase | RegexOptions.Singleline),
         string.Empty),
        (new(@"<dnn:LINKTOMOBILE\s[^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
         string.Empty),
        (new(@"<dnn:TOAST\s[^>]*/?>",       RegexOptions.IgnoreCase | RegexOptions.Singleline),
         string.Empty),
        (new(@"<dnn:CONTROLPANEL\s[^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline),
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

    // Captures the id attribute value from an HTML element.
    private static readonly Regex DivIdAttributeRegex =
        new(@"\bid=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches any HTML element (not <dnn:...>) that still carries runat="server".
    // Used to add the dnn_ naming-container prefix to the id attribute before
    // runat="server" is stripped, so the output matches DNN's rendered HTML.
    private static readonly Regex HtmlRunatServerElementRegex =
        new(@"<(?!dnn:)[A-Za-z]+\s[^>]*runat=""server""[^>]*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches any remaining self-closing <dnn:TAGNAME .../> controls.
    private static readonly Regex DnnSelfClosingTagRegex =
        new(@"<dnn:[A-Za-z]+\s[^>]*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches any remaining open/close <dnn:TAGNAME> ... </dnn:TAGNAME> pairs.
    private static readonly Regex DnnOpenCloseTagRegex =
        new(@"</?dnn:[A-Za-z]+[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Adds the <c>dnn_</c> naming-container prefix to any <c>id="…"</c>
    /// attribute found in <paramref name="tag"/>, unless the value already
    /// starts with <c>dnn_</c>.  Returns the modified tag string.
    /// </summary>
    private static string PrefixIdWithDnn(string tag)
    {
        return DivIdAttributeRegex.Replace(tag, m =>
        {
            string idVal = m.Groups[1].Value;
            return idVal.StartsWith("dnn_", StringComparison.OrdinalIgnoreCase)
                ? m.Value
                : $"id=\"dnn_{idVal}\"";
        });
    }

    // Matches <dnn:DnnCssInclude ... FilePath="..." .../> and captures the FilePath value.
    private static readonly Regex DnnCssIncludeRegex =
        new(@"<dnn:DnnCssInclude\s[^>]*FilePath=""([^""]+)""[^>]*/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Matches <dnn:DnnJsInclude ... FilePath="..." .../> and captures the FilePath value.
    private static readonly Regex DnnJsIncludeRegex =
        new(@"<dnn:DnnJsInclude\s[^>]*FilePath=""([^""]+)""[^>]*/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Matches <%= SkinPath %> ASP.NET expressions (with optional surrounding whitespace).
    // DNN skins use this expression in <script src> and <link> tags to reference
    // files relative to the skin folder.
    private static readonly Regex SkinPathExpressionRegex =
        new(@"<%=\s*SkinPath\s*%>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches <%@ Register ... %> directives in DNN ASCX files.
    private static readonly Regex RegisterDirectiveRegex =
        new(@"<%@\s*Register\s[^%]*(?:%(?!>)[^%]*)*%>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Attribute-level regex helpers for <%@ Register %> parsing.
    private static readonly Regex TagPrefixAttrRegex =
        new(@"\bTagPrefix=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TagNameAttrRegex =
        new(@"\bTagName=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SrcAttrRegex =
        new(@"\bSrc=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches <dnn:ICON .../> or <dnn:Icon .../> (DNN container icon control).
    private static readonly Regex DnnIconRegex =
        new(@"<dnn:Icon\s[^>]*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

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
    ///   <item><c>&lt;dnn:ICON … /&gt;</c> is replaced with a conditional <c>&lt;img&gt;</c>
    ///         tag referencing <c>$!{dotContent.image}</c> so that the module icon renders.</item>
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
        // Replace <dnn:Icon> with a conditional <img> referencing the content
        // image field.  Preserve style/loading attributes from the original tag.
        html = DnnIconRegex.Replace(html, m =>
        {
            var styleMatch = Regex.Match(m.Value, @"style=""([^""]*)""", RegexOptions.IgnoreCase);
            string style = styleMatch.Success ? $@" style=""{styleMatch.Groups[1].Value}""" : "";
            var loadMatch = Regex.Match(m.Value, @"loading=""([^""]*)""", RegexOptions.IgnoreCase);
            string loading = loadMatch.Success ? $@" loading=""{loadMatch.Groups[1].Value}""" : "";
            // Velocity conditional: only render the <img> when the image field has a value.
            return "#if(\"$!{dotContent.image}\" != \"\")"
                 + $" <img src=\"$!{{dotContent.image}}\" alt=\"$!{{dotContent.title}}\"{style}{loading} />"
                 + " #end";
        });
        html = ContentPaneRegex.Replace(html, "$!{dotContent.body}");
        // Add the dnn_ naming-container prefix to IDs on elements that had
        // runat="server", matching the rendered DNN page behaviour.
        html = HtmlRunatServerElementRegex.Replace(html, m =>
        {
            string tag = RunatServerRegex.Replace(m.Value, string.Empty);
            return PrefixIdWithDnn(tag);
        });
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
    public static (string Body, string Header, IReadOnlyDictionary<string, int> PaneUuidMap) ConvertAscxToTemplateHtml(
        string ascx,
        string defaultContainerId = "",
        string themeName = "",
        string skinName = "",
        IReadOnlySet<string>? availableThemeFiles = null)
    {
        string html = DirectiveRegex.Replace(ascx, string.Empty);

        // Replace <%= SkinPath %> expressions with the theme base URL before
        // CodeBlockRegex strips all <% ... %> blocks.  DNN skins use this
        // ASP.NET expression in <script src> and <link> tags to reference
        // files relative to the skin folder (e.g. custom.js, modal.js).
        if (!string.IsNullOrWhiteSpace(themeName))
            html = SkinPathExpressionRegex.Replace(html, $"/application/themes/{themeName}/");

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

        // Convert <dnn:DnnCssInclude FilePath="..." /> to <link> tags and
        // <dnn:DnnJsInclude FilePath="..." /> to <script> tags in the body.
        // The FilePath attribute is relative to the skin folder, which maps
        // to /application/themes/{themeName}/ in DotCMS.  This must run
        // before the generic DNN-tag cleanup that follows.
        var cssTags = new List<string>();
        if (!string.IsNullOrWhiteSpace(themeName))
        {
            string themeBase = $"/application/themes/{themeName}/";
            html = DnnCssIncludeRegex.Replace(html, m =>
            {
                cssTags.Add($@"<link rel=""stylesheet"" href=""{themeBase}{m.Groups[1].Value}"" />");
                return string.Empty;
            });
            html = DnnJsIncludeRegex.Replace(html,
                m => $@"<script src=""{themeBase}{m.Groups[1].Value}""></script>");
        }

        // Replace DNN server-side pane divs (runat="server") with #parseContainer
        // directives so DotCMS renders container content in those zones.
        // Also build a mapping of pane id → uuid so that content modules can
        // be placed in the correct container slot via multiTree.
        // This must run before RunatServerRegex strips the runat attribute.
        //
        // When a pane div carries layout-relevant attributes (CSS classes,
        // styles, etc.) beyond the standard id and runat, the outer <div> is
        // preserved so that bootstrap / layout styling is not lost.  The
        // #parseContainer directive is placed *inside* the wrapper div.
        var paneUuidMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(defaultContainerId))
        {
            int uuid = 0;
            html = DnnRunatPaneDivRegex.Replace(html, m =>
            {
                ++uuid;
                Match idMatch = DivIdAttributeRegex.Match(m.Value);
                if (idMatch.Success)
                    paneUuidMap[idMatch.Groups[1].Value] = uuid;

                string parseContainer = $"#parseContainer('{defaultContainerId}', '{uuid}')";

                // Extract just the opening <div ...> tag.
                string openTag;
                bool matchedClosing = m.Groups[1].Success;
                if (matchedClosing)
                {
                    int closingOffset = m.Groups[1].Index - m.Index;
                    openTag = m.Value[..closingOffset];
                }
                else
                {
                    openTag = m.Value;
                }

                // Build a cleaned version (no runat="server") and check
                // whether layout-relevant attributes remain (e.g. class, style).
                string cleaned = RunatServerRegex.Replace(openTag, string.Empty);

                // Add the dnn_ prefix to the id attribute so that the output
                // matches the rendered DNN page (ASP.NET prepends the naming-
                // container prefix "dnn_" to server-control IDs at runtime).
                cleaned = PrefixIdWithDnn(cleaned);

                string withoutId = DivIdAttributeRegex.Replace(cleaned, string.Empty);
                bool hasLayoutAttrs = withoutId.Contains('=');

                if (hasLayoutAttrs)
                {
                    return matchedClosing
                        ? $"{cleaned}\n{parseContainer}\n</div>"
                        : $"{cleaned}\n{parseContainer}";
                }

                return parseContainer;
            });
        }
        else
        {
            // No container available: remove pane divs entirely.
            html = DnnRunatPaneDivRegex.Replace(html, string.Empty);
        }

        // For any remaining HTML elements that still carry runat="server"
        // (e.g. non-pane wrapper divs), add the dnn_ naming-container prefix
        // to their id attribute to match the rendered DNN page.
        html = HtmlRunatServerElementRegex.Replace(html, m =>
        {
            string tag = RunatServerRegex.Replace(m.Value, string.Empty);
            return PrefixIdWithDnn(tag);
        });
        html = RunatServerRegex.Replace(html, string.Empty);
        html = DnnSelfClosingTagRegex.Replace(html, string.Empty);
        html = DnnOpenCloseTagRegex.Replace(html, string.Empty);
        html = html.Trim();

        // Helper to check whether a href is already referenced in the body
        // or in the CSS tags collected so far.
        bool alreadyReferenced(string href) =>
            html.Contains(href, StringComparison.OrdinalIgnoreCase) ||
            cssTags.Any(t => t.Contains(href, StringComparison.OrdinalIgnoreCase));

        // DNN automatically loads a per-skin CSS file that matches the
        // skin filename (e.g. Home.css for Home.ascx).  Add the link
        // BEFORE the skin.css injection below so that skin.css ends
        // up first in the output (matching DNN's load order: skin.css base
        // styles first, per-skin overrides second).  Skip when the skin
        // name is "skin" (already covered) or when already referenced.
        // Unlike skin.css, the per-skin link is always injected regardless
        // of availableThemeFiles because WriteThemeFileAssets creates a
        // placeholder CSS file when the export does not include one.
        if (!string.IsNullOrWhiteSpace(themeName) &&
            !string.IsNullOrWhiteSpace(skinName) &&
            !string.Equals(skinName, "skin", StringComparison.OrdinalIgnoreCase))
        {
            string skinCssHref = $"/application/themes/{themeName}/{skinName}.css";
            if (!alreadyReferenced(skinCssHref))
            {
                string skinCssLink = $@"<link rel=""stylesheet"" href=""{skinCssHref}"" />";
                cssTags.Add(skinCssLink);
            }
        }

        // Add a <link> tag for the theme's main skin.css.  DNN
        // automatically included the skin CSS via its own framework; in
        // DotCMS we must add it explicitly.  Insert at position 0 so
        // skin.css appears first — matching DNN's load order.  Skip when
        // a DnnCssInclude directive already emitted a skin.css link above,
        // or when an availableThemeFiles set is provided and skin.css is
        // not present in the export.
        if (!string.IsNullOrWhiteSpace(themeName))
        {
            string skinCssHref = $"/application/themes/{themeName}/skin.css";
            if (!alreadyReferenced(skinCssHref) &&
                (availableThemeFiles is null || availableThemeFiles.Contains(skinCssHref.TrimStart('/'))))
            {
                string cssLink = $@"<link rel=""stylesheet"" href=""{skinCssHref}"" />";
                cssTags.Insert(0, cssLink);
            }
        }

        // Prepend collected CSS <link> tags to the body so they appear
        // before the template content.  The header field is left empty
        // because DotCMS push-publish does not reliably render it.
        if (cssTags.Count > 0)
            html = string.Join("\n", cssTags) + "\n" + html;

        return (html, string.Empty, paneUuidMap);
    }

    // ------------------------------------------------------------------
    // Theme static-asset helper
    // ------------------------------------------------------------------

    /// <summary>
    /// Write theme static file assets to the bundle.  Reads non-ASCX static
    /// files from <paramref name="themesZipPath"/>, builds the DotCMS folder
    /// hierarchy, and writes each file as a proper <c>FileAsset</c> contentlet
    /// so that DotCMS's push-publish importer creates the
    /// <c>/application/themes/</c> directory tree and places the files
    /// correctly.  When <paramref name="templateDefs"/> is supplied, per-skin
    /// CSS files (e.g. <c>Home.css</c> for <c>Home.ascx</c>) are created for
    /// any skin template that does not already have a matching CSS file in the
    /// theme export.  When <paramref name="portalFiles"/> are provided, any
    /// per-skin CSS file that matches a portal file by name will use the real
    /// content from the portal file instead of an empty placeholder.  Consumed
    /// portal-file identifiers are added to
    /// <paramref name="consumedPortalFileIds"/> so callers can avoid writing
    /// those files a second time at the site root.
    /// </summary>
    private static void WriteThemeFileAssets(
        TarWriter tar,
        string themesZipPath,
        string hostId,
        string? siteId,
        string? siteInode,
        string contentWorkDir,
        List<(string type, string id, string inode, string name, string site, string folder)> manifestEntries,
        IReadOnlyList<(string id, string inode, string name, string html, string header, string themeName, IReadOnlyDictionary<string, int> paneUuidMap)>? templateDefs = null,
        IReadOnlyList<DnnPortalFile>? portalFiles = null,
        ISet<string>? consumedPortalFileIds = null,
        Dictionary<string, string>? unifiedFolderInodes = null,
        HashSet<string>? writtenFolderPaths = null)
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

        // Create per-skin CSS files for templates that do not already have
        // a matching CSS file in the theme export.  DNN auto-loads
        // {SkinName}.css alongside each skin ASCX; the link is always
        // injected in the template HTML so the file must exist in DotCMS.
        //
        // When portal files are available we attempt to resolve the
        // per-skin CSS from there (matched by filename, case-insensitive).
        // This handles the common case where a per-skin CSS file lives in
        // the DNN portal root (export_files.zip) rather than in the theme
        // folder (export_themes.zip).  If no matching portal file is found,
        // an empty placeholder is created instead.
        if (templateDefs is not null)
        {
            var existingPaths = new HashSet<string>(
                themeFiles.Select(f => f.relPath),
                StringComparer.OrdinalIgnoreCase);

            // Build a filename → portal-file lookup so we can resolve
            // missing theme CSS files from any available export source.
            var portalFilesByName = new Dictionary<string, DnnPortalFile>(
                StringComparer.OrdinalIgnoreCase);
            if (portalFiles is not null)
            {
                foreach (DnnPortalFile pf in portalFiles)
                {
                    // First match wins; prefer files closer to the portal
                    // root (empty FolderPath) over files in sub-folders.
                    portalFilesByName.TryAdd(pf.FileName, pf);
                }
            }

            foreach (var (_, _, name, _, _, themeName, _) in templateDefs)
            {
                if (string.IsNullOrWhiteSpace(themeName) ||
                    string.IsNullOrWhiteSpace(name) ||
                    string.Equals(name, "skin", StringComparison.OrdinalIgnoreCase))
                    continue;

                string cssRelPath = $"application/themes/{themeName}/{name}.css";
                if (!existingPaths.Contains(cssRelPath))
                {
                    string fileName = $"{name}.css";
                    // USTAR tar format has a max path length of 256 characters.
                    // BuildAssetPath adds ~58 characters of overhead, so skip
                    // placeholder creation when the filename is too long.
                    if (fileName.Length > 190)
                        continue;

                    // Attempt to resolve from portal files first.
                    byte[] content;
                    if (portalFilesByName.TryGetValue(fileName, out DnnPortalFile? match))
                    {
                        content = match.Content;
                        // Mark the portal file as consumed so it is not also
                        // written at the site root.
                        consumedPortalFileIds?.Add(match.UniqueId);
                    }
                    else
                    {
                        content = "/* Per-skin CSS placeholder */\n"u8.ToArray();
                    }

                    themeFiles.Add((cssRelPath, fileName, content));
                    existingPaths.Add(cssRelPath);
                }
            }

            // Ensure the portal logo image is placed in each theme's Images/
            // directory.  ConvertAscxToTemplateHtml replaces <dnn:LOGO> with
            // <img src="/application/themes/{themeName}/Images/logo.png">, but
            // the actual logo file often lives in the portal's Images/ folder
            // (export_files.zip) rather than in the skin directory inside
            // export_themes.zip.  Without this step the template references a
            // file that does not exist in the bundle.
            //
            // Mark the portal logo as consumed so it is placed only in
            // the theme's Images/ directory and not duplicated at the
            // site root (ROOT/Images/logo.png).
            var resolvedThemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, _, _, _, _, themeName, _) in templateDefs)
            {
                if (string.IsNullOrWhiteSpace(themeName) || !resolvedThemes.Add(themeName))
                    continue;

                string logoRelPath = $"application/themes/{themeName}/Images/logo.png";
                if (existingPaths.Contains(logoRelPath))
                    continue;

                // Look for logo.png in the portal files – prefer the Images/
                // folder, but fall back to the portal root (empty FolderPath)
                // since some DNN exports place the logo at the root level.
                if (portalFiles is not null)
                {
                    DnnPortalFile? logoFile = portalFiles.FirstOrDefault(pf =>
                        pf.FileName.Equals("logo.png", StringComparison.OrdinalIgnoreCase) &&
                        pf.FolderPath.TrimEnd('/').Equals("Images", StringComparison.OrdinalIgnoreCase));

                    logoFile ??= portalFiles.FirstOrDefault(pf =>
                        pf.FileName.Equals("logo.png", StringComparison.OrdinalIgnoreCase) &&
                        string.IsNullOrEmpty(pf.FolderPath));

                    if (logoFile is not null)
                    {
                        themeFiles.Add((logoRelPath, "logo.png", logoFile.Content));
                        existingPaths.Add(logoRelPath);
                        consumedPortalFileIds?.Add(logoFile.UniqueId);
                    }
                }
            }
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
                string dotcmsKey = "/" + partialPath + "/";
                // Reuse an existing folder inode from the unified
                // registry to avoid duplicate identifier entries.
                if (unifiedFolderInodes is not null
                    && unifiedFolderInodes.TryGetValue(dotcmsKey, out string? existingInode))
                {
                    folderInodes.TryAdd(partialPath, existingInode);
                }
                else
                {
                    if (!folderInodes.ContainsKey(partialPath))
                    {
                        string newInode = Guid.NewGuid().ToString();
                        folderInodes[partialPath] = newInode;
                        unifiedFolderInodes?.TryAdd(dotcmsKey, newInode);
                    }
                }
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

                // Skip folders already written by other folder-building
                // phases to avoid duplicate identifier entries.
                if (writtenFolderPaths is not null && !writtenFolderPaths.Add(dotcmsPath))
                    continue;

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
        // "FidelityBankTexas/skins/fbot/CSS/bootstrap.min.css"
        //   → "ROOT/application/themes/fbot/CSS/bootstrap.min.css"
        // "FidelityBankTexas/containers/fbot/card.ascx"
        //   → "ROOT/application/themes/fbot/Containers/card.ascx"
        //
        // The ROOT/ prefix causes DotCMS to write these files into the
        // /application/themes/ directory on the file system, which is where
        // DotCMS expects theme static assets to live.
        const string skinsPrefix      = "_default/Skins/";
        const string containersPrefix = "_default/Containers/";

        // Standard _default/ format.
        if (zipEntryPath.StartsWith(skinsPrefix, StringComparison.OrdinalIgnoreCase))
            return "ROOT/application/themes/" + zipEntryPath[skinsPrefix.Length..];

        if (zipEntryPath.StartsWith(containersPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string rest = zipEntryPath[containersPrefix.Length..];
            int slash = rest.IndexOf('/');
            return slash < 0
                ? "ROOT/application/themes/" + rest
                : $"ROOT/application/themes/{rest[..slash]}/Containers/{rest[(slash + 1)..]}";
        }

        // Non-_default format: {Package}/skins/{Theme}/{rest}
        // Some DNN exports place skins under the package name instead of
        // _default/, e.g. "FidelityBankTexas/skins/fbot/skin.css".
        int skinsIdx = zipEntryPath.IndexOf("/skins/", StringComparison.OrdinalIgnoreCase);
        if (skinsIdx >= 0)
            return "ROOT/application/themes/" + zipEntryPath[(skinsIdx + "/skins/".Length)..];

        // Non-_default format: {Package}/containers/{Theme}/{rest}
        int containersIdx = zipEntryPath.IndexOf("/containers/", StringComparison.OrdinalIgnoreCase);
        if (containersIdx >= 0)
        {
            string rest = zipEntryPath[(containersIdx + "/containers/".Length)..];
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
    /// spaces (and other non-alphanumeric characters) with hyphens.
    /// For example: <c>"My Website"</c> → <c>"My-Website"</c>.
    /// </summary>
    public static string SanitizeHostname(string siteName)
    {
        // Replace runs of non-alphanumeric characters (including spaces) with a hyphen.
        string sanitized = Regex.Replace(siteName, @"[^A-Za-z0-9]+", "-");
        sanitized = sanitized.Trim('-');
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

    /// <summary>
    /// Generates a deterministic UUID from <paramref name="seed"/>.  The same
    /// seed always produces the same UUID, which is critical for content type
    /// and field IDs: bundles from different DNN sites that share the same DNN
    /// module must produce the same content type ID so DotCMS treats the second
    /// import as an <em>update</em> instead of a conflicting insert.
    /// </summary>
    public static string DeterministicId(string seed)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        // Shape the first 16 bytes into a UUID-like value with version
        // and variant bits set for compatibility with UUID parsers.
        // Note: this is NOT a strict RFC 4122 UUID v5 (which uses SHA-1);
        // SHA-256 provides better collision resistance.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16]).ToString();
    }

    /// <summary>
    /// Returns the identifier of the best default container from a list of
    /// container definitions.  Prefers a container named "standard"
    /// (case-insensitive) because it uses minimal markup.  Falls back to the
    /// first container if no "standard" container exists.
    /// </summary>
    internal static string ResolveDefaultContainerId(
        IReadOnlyList<(string id, string inode, string name, string html, string themeName)> containerDefs)
    {
        if (containerDefs.Count == 0)
            return string.Empty;

        foreach (var c in containerDefs)
        {
            if (c.name.Equals("standard", StringComparison.OrdinalIgnoreCase))
                return c.id;
        }

        return containerDefs[0].id;
    }

    /// <summary>
    /// Resolves a DNN container source path (e.g.
    /// <c>[L]Containers/FBOT/hpcard.ascx</c>) to a DotCMS container
    /// identifier by matching the theme-name and container-name segments
    /// against <paramref name="containerDefs"/>.  Falls back to
    /// <paramref name="defaultContainerId"/> when no match is found.
    /// </summary>
    internal static string ResolveContainerIdFromSrc(
        string containerSrc,
        IReadOnlyList<(string id, string inode, string name, string html, string themeName)> containerDefs,
        string defaultContainerId)
    {
        if (string.IsNullOrWhiteSpace(containerSrc) || containerDefs.Count == 0)
            return defaultContainerId;

        // DNN container paths look like:
        //   [L]Containers/{ThemeName}/{ContainerName}.ascx
        //   [G]Containers/{ThemeName}/{ContainerName}.ascx
        // Strip the [L] / [G] prefix, then extract theme and container name.
        string path = containerSrc;
        if (path.Length > 3 && path[0] == '[' && path[2] == ']')
            path = path[3..];

        // Normalise separators and locate the "containers/" segment.
        path = path.Replace('\\', '/');
        int idx = path.IndexOf("containers/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return defaultContainerId;

        string rest = path[(idx + "containers/".Length)..];
        int slash = rest.IndexOf('/');
        if (slash < 0)
            return defaultContainerId;

        string srcTheme = rest[..slash];
        string srcName  = Path.GetFileNameWithoutExtension(rest[(slash + 1)..]);

        // Try matching theme + name first, then just name.
        foreach (var c in containerDefs)
        {
            if (c.name.Equals(srcName, StringComparison.OrdinalIgnoreCase) &&
                c.themeName.Equals(srcTheme, StringComparison.OrdinalIgnoreCase))
                return c.id;
        }
        foreach (var c in containerDefs)
        {
            if (c.name.Equals(srcName, StringComparison.OrdinalIgnoreCase))
                return c.id;
        }

        return defaultContainerId;
    }
}
