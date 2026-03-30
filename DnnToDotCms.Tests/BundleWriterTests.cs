using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using DnnToDotCms.Bundle;
using DnnToDotCms.Models;

namespace DnnToDotCms.Tests;

public class BundleWriterTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static DotCmsContentType MakeHtmlContentType() => new()
    {
        Clazz       = "com.dotcms.contenttype.model.type.SimpleContentType",
        Name        = "HTMLContent",
        Variable    = "htmlContent",
        Description = "Converted from DNN HTML module",
        Icon        = "fa fa-code",
        Fields      =
        [
            new DotCmsField
            {
                Clazz    = "com.dotcms.contenttype.model.field.TextField",
                Name     = "Title",
                Variable = "title",
                DataType = "TEXT",
                Indexed  = true,
                Required = true,
                Listed   = true,
                Searchable = true,
            },
            new DotCmsField
            {
                Clazz    = "com.dotcms.contenttype.model.field.WysiwygField",
                Name     = "Body",
                Variable = "body",
                DataType = "LONG_TEXT",
                Indexed  = true,
                Required = true,
                Searchable = true,
            },
        ],
    };

    /// <summary>
    /// Write the bundle to an in-memory stream and list all tar entry names.
    /// </summary>
    private static (MemoryStream stream, List<string> entryNames) WriteBundleToMemory(
        IReadOnlyList<DotCmsContentType> contentTypes,
        string? themesZipPath = null)
    {
        var ms = new MemoryStream();
        BundleWriter.Write(contentTypes, ms, themesZipPath);
        ms.Position = 0;

        var names = new List<string>();
        using var gz  = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gz);

        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
            names.Add(entry.Name);

        ms.Position = 0;
        return (ms, names);
    }

    /// <summary>Read the raw content of a specific tar entry.</summary>
    private static string? ReadTarEntry(MemoryStream bundleStream, string entryName)
    {
        bundleStream.Position = 0;
        using var gz  = new GZipStream(bundleStream, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gz);

        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            if (entry.Name == entryName && entry.DataStream is not null)
            {
                using var sr = new StreamReader(entry.DataStream, leaveOpen: true);
                return sr.ReadToEnd();
            }
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Bundle structure tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_ProducesValidGzippedTar()
    {
        var ms = new MemoryStream();
        BundleWriter.Write([MakeHtmlContentType()], ms);
        ms.Position = 0;

        // Must decompress without exception
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        Assert.NotNull(tar.GetNextEntry());
    }

    [Fact]
    public void Write_IncludesManifestCsv()
    {
        var (_, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        Assert.Contains("manifest.csv", names);
    }

    [Fact]
    public void Write_IncludesOneContentTypeFilePerContentType()
    {
        var contentTypes = new[]
        {
            MakeHtmlContentType(),
            new DotCmsContentType { Name = "Event", Variable = "event" },
        };

        var (_, names) = WriteBundleToMemory(contentTypes);

        int count = names.Count(n =>
            n.StartsWith("working/System Host/") && n.EndsWith(".contentType.json"));

        Assert.Equal(2, count);
    }

    [Fact]
    public void Write_EmptyContentTypeList_ProducesManifestOnly()
    {
        var (_, names) = WriteBundleToMemory([]);
        Assert.Single(names, "manifest.csv");
    }

    // ------------------------------------------------------------------
    // manifest.csv content tests
    // ------------------------------------------------------------------

    [Fact]
    public void Manifest_ContainsBundleIdHeader()
    {
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        string manifest = ReadTarEntry(ms, "manifest.csv")!;
        Assert.StartsWith("#Bundle ID:", manifest);
    }

    [Fact]
    public void Manifest_ContainsPublishOperationHeader()
    {
        var (ms, _) = WriteBundleToMemory([MakeHtmlContentType()]);
        string manifest = ReadTarEntry(ms, "manifest.csv")!;
        Assert.Contains("#Operation:PUBLISH", manifest);
    }

    [Fact]
    public void Manifest_ContainsIncludedContentTypeRow()
    {
        var (ms, _) = WriteBundleToMemory([MakeHtmlContentType()]);
        string manifest = ReadTarEntry(ms, "manifest.csv")!;
        Assert.Contains("INCLUDED,contenttype,", manifest);
        Assert.Contains("HTMLContent", manifest);
        // Site column must be "System Host" (display name), NOT the internal
        // identifier "SYSTEM_HOST".  DotCMS resolves the bundle file path using
        // the site column value, so an incorrect value causes 0 content to
        // be imported.
        Assert.Contains("System Host", manifest);
        Assert.DoesNotContain("SYSTEM_HOST", manifest);
    }

    // ------------------------------------------------------------------
    // contentType.json content tests
    // ------------------------------------------------------------------

    private static JsonDocument ParseFirstContentTypeJson(MemoryStream ms, List<string> names)
    {
        string entryName = names.First(n =>
            n.StartsWith("working/System Host/") && n.EndsWith(".contentType.json"));
        string json = ReadTarEntry(ms, entryName)!;
        return JsonDocument.Parse(json);
    }

    [Fact]
    public void ContentTypeJson_HasContentTypeWrapper()
    {
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        using var doc = ParseFirstContentTypeJson(ms, names);
        Assert.True(doc.RootElement.TryGetProperty("contentType", out _));
    }

    [Fact]
    public void ContentTypeJson_ContentType_HasImmutableClazz()
    {
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        using var doc = ParseFirstContentTypeJson(ms, names);

        string clazz = doc.RootElement
            .GetProperty("contentType").GetProperty("clazz").GetString()!;

        Assert.Contains("Immutable", clazz);
    }

    [Fact]
    public void ContentTypeJson_ContentType_HasId()
    {
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        using var doc = ParseFirstContentTypeJson(ms, names);

        string id = doc.RootElement
            .GetProperty("contentType").GetProperty("id").GetString()!;

        Assert.True(Guid.TryParse(id, out _));
    }

    [Fact]
    public void ContentTypeJson_ContentType_HasSystemHostAndFolder()
    {
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        using var doc = ParseFirstContentTypeJson(ms, names);

        JsonElement ct = doc.RootElement.GetProperty("contentType");
        Assert.Equal("SYSTEM_HOST",   ct.GetProperty("host").GetString());
        Assert.Equal("SYSTEM_FOLDER", ct.GetProperty("folder").GetString());
    }

    [Fact]
    public void ContentTypeJson_HasFieldsArray()
    {
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        using var doc = ParseFirstContentTypeJson(ms, names);

        JsonElement fields = doc.RootElement.GetProperty("fields");
        Assert.Equal(JsonValueKind.Array, fields.ValueKind);
        Assert.True(fields.GetArrayLength() > 0);
    }

    [Fact]
    public void ContentTypeJson_Fields_StartWithLayoutFields()
    {
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        using var doc = ParseFirstContentTypeJson(ms, names);

        JsonElement fields = doc.RootElement.GetProperty("fields");
        string first  = fields[0].GetProperty("clazz").GetString()!;
        string second = fields[1].GetProperty("clazz").GetString()!;

        Assert.Contains("ImmutableRowField",    first);
        Assert.Contains("ImmutableColumnField", second);
    }

    [Fact]
    public void ContentTypeJson_Fields_DataFieldsHaveImmutableClazz()
    {
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        using var doc = ParseFirstContentTypeJson(ms, names);

        JsonElement fields = doc.RootElement.GetProperty("fields");
        // Skip layout fields (index 0 and 1)
        foreach (JsonElement field in fields.EnumerateArray().Skip(2))
        {
            string clazz = field.GetProperty("clazz").GetString()!;
            Assert.Contains("Immutable", clazz);
        }
    }

    [Fact]
    public void ContentTypeJson_Fields_HaveDbColumn()
    {
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        using var doc = ParseFirstContentTypeJson(ms, names);

        JsonElement fields = doc.RootElement.GetProperty("fields");
        // The TextField should get "text1"; WysiwygField should get "text_area1"
        JsonElement titleField = fields[2];
        JsonElement bodyField  = fields[3];

        Assert.Equal("text1",      titleField.GetProperty("dbColumn").GetString());
        Assert.Equal("text_area1", bodyField.GetProperty("dbColumn").GetString());
    }

    [Fact]
    public void ContentTypeJson_HasWorkflowSchemaIds()
    {
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        using var doc = ParseFirstContentTypeJson(ms, names);

        JsonElement ids = doc.RootElement.GetProperty("workflowSchemaIds");
        Assert.Equal(JsonValueKind.Array, ids.ValueKind);
        Assert.Equal(1, ids.GetArrayLength());
        Assert.Equal("d61a59e1-a49c-46f2-a929-db2b4bfa88b2", ids[0].GetString());
    }

    [Fact]
    public void ContentTypeJson_OperationIsPublish()
    {
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        using var doc = ParseFirstContentTypeJson(ms, names);

        Assert.Equal("PUBLISH", doc.RootElement.GetProperty("operation").GetString());
    }

    // ------------------------------------------------------------------
    // ToImmutableClazz utility tests
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(
        "com.dotcms.contenttype.model.field.TextField",
        "com.dotcms.contenttype.model.field.ImmutableTextField")]
    [InlineData(
        "com.dotcms.contenttype.model.field.WysiwygField",
        "com.dotcms.contenttype.model.field.ImmutableWysiwygField")]
    [InlineData(
        "com.dotcms.contenttype.model.type.SimpleContentType",
        "com.dotcms.contenttype.model.type.ImmutableSimpleContentType")]
    [InlineData(
        "com.dotcms.contenttype.model.field.ImmutableTextField",
        "com.dotcms.contenttype.model.field.ImmutableTextField")]  // already Immutable
    public void ToImmutableClazz_ConvertsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, BundleWriter.ToImmutableClazz(input));
    }

    // ------------------------------------------------------------------
    // Error / edge-case tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_InvalidThemesZipPath_DoesNotThrow()
    {
        // A non-existent path is simply ignored (no themes included).
        var ms = new MemoryStream();
        BundleWriter.Write([MakeHtmlContentType()], ms, "/nonexistent/themes.zip");
        ms.Position = 0;
        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void Write_CorruptThemesZip_DoesNotThrowAndStillWritesContentTypes()
    {
        // Create a file that is not a valid ZIP archive.
        string fakePath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(fakePath, [0x00, 0x01, 0x02, 0x03]);

            var ms = new MemoryStream();
            // Should complete without throwing even though the zip is corrupt.
            BundleWriter.Write([MakeHtmlContentType()], ms, fakePath);
            ms.Position = 0;

            var (_, names) = WriteBundleToMemory([MakeHtmlContentType()]);
            Assert.Contains("manifest.csv", names);
            int ctCount = names.Count(n =>
                n.StartsWith("working/System Host/") && n.EndsWith(".contentType.json"));
            Assert.Equal(1, ctCount);
        }
        finally
        {
            File.Delete(fakePath);
        }
    }

    [Fact]
    public void Write_MultipleContentTypes_EachHasUniqueId()
    {
        var contentTypes = new[]
        {
            MakeHtmlContentType(),
            new DotCmsContentType { Name = "Event", Variable = "event" },
        };

        var ms = new MemoryStream();
        BundleWriter.Write(contentTypes, ms);
        ms.Position = 0;

        using var gz  = new GZipStream(ms, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        var ids = new List<string>();
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            if (entry.Name.StartsWith("working/System Host/") &&
                entry.Name.EndsWith(".contentType.json"))
            {
                // Extract the UUID from the file name
                string name = Path.GetFileNameWithoutExtension(
                    Path.GetFileNameWithoutExtension(entry.Name));
                ids.Add(name);
            }
        }

        Assert.Equal(2, ids.Distinct().Count());
    }

    // ------------------------------------------------------------------
    // Themes ZIP helper — builds an in-memory export_themes.zip with
    // a skin and a container ASCX, matching the DNN export layout.
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a minimal in-memory themes ZIP that contains one container and
    /// one skin ASCX, replicating the <c>_default/Containers/</c> and
    /// <c>_default/Skins/</c> structure produced by DNN Export.
    /// </summary>
    private static string BuildThemesZip(
        string containerAscx = """
            <%@ Control Inherits="DotNetNuke.UI.Containers.Container" %>
            <%@ Register TagPrefix="dnn" TagName="TITLE" Src="~/Admin/Containers/Title.ascx" %>
            <div class="DNNContainer_Boxed">
                <h2><dnn:TITLE runat="server" id="dnnTITLE" /></h2>
                <div id="ContentPane" runat="server"></div>
            </div>
            """,
        string skinAscx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <%@ Register TagPrefix="dnn" TagName="LOGO" Src="~/Admin/Skins/Logo.ascx" %>
            <%@ Register TagPrefix="dnn" TagName="MENU" Src="~/DesktopModules/DDRMenu/Menu.ascx" %>
            <div id="siteWrapper">
                <dnn:LOGO runat="server" id="dnnLOGO" />
                <dnn:MENU runat="server" id="dnnMENU" />
                <div id="ContentPane" runat="server"></div>
            </div>
            """)
    {
        string path = Path.GetTempFileName() + ".zip";
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            ZipArchiveEntry containerEntry =
                zip.CreateEntry("_default/Containers/TestTheme/Boxed.ascx");
            using (var w = new StreamWriter(containerEntry.Open()))
                w.Write(containerAscx);

            ZipArchiveEntry skinEntry =
                zip.CreateEntry("_default/Skins/TestTheme/Home.ascx");
            using (var w = new StreamWriter(skinEntry.Open()))
                w.Write(skinAscx);
        }
        return path;
    }

    // ------------------------------------------------------------------
    // Container bundle entry tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithThemesZip_IncludesContainerXmlEntry()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (_, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);

            Assert.Contains(names,
                n => n.StartsWith("live/System Host/") &&
                     n.EndsWith(".containers.container.xml"));
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithThemesZip_ContainerXmlHasCorrectRootElement()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);
            string entryName = names.First(
                n => n.EndsWith(".containers.container.xml"));
            string xml = ReadTarEntry(ms, entryName)!;

            Assert.Contains("<com.dotcms.publisher.pusher.wrapper.ContainerWrapper>", xml);
            Assert.Contains("<operation>PUBLISH</operation>", xml);
            Assert.Contains("<assetType>containers</assetType>", xml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithThemesZip_ContainerXmlIncludesConvertedCode()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);
            string entryName = names.First(n => n.EndsWith(".containers.container.xml"));
            string xml = ReadTarEntry(ms, entryName)!;

            // <dnn:TITLE> should have been converted to $dotContent.title
            Assert.Contains("$dotContent.title", xml);
            // ContentPane div should have been replaced with $!{dotContent.body}
            Assert.Contains("$!{dotContent.body}", xml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithThemesZip_ManifestIncludesContainersRow()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);
            string manifest = ReadTarEntry(ms, "manifest.csv")!;

            Assert.Contains("INCLUDED,containers,", manifest);
        }
        finally { File.Delete(zipPath); }
    }

    // ------------------------------------------------------------------
    // Template bundle entry tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithThemesZip_IncludesTemplateXmlEntry()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (_, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);

            Assert.Contains(names,
                n => n.StartsWith("live/System Host/") &&
                     n.EndsWith(".template.template.xml"));
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithThemesZip_TemplateXmlHasCorrectRootElement()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);
            string entryName = names.First(n => n.EndsWith(".template.template.xml"));
            string xml = ReadTarEntry(ms, entryName)!;

            Assert.Contains("<com.dotcms.publisher.pusher.wrapper.TemplateWrapper>", xml);
            Assert.Contains("<operation>PUBLISH</operation>", xml);
            Assert.Contains("<assetType>template</assetType>", xml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithThemesZip_TemplateXmlBodyContainsConvertedHtml()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);
            string entryName = names.First(n => n.EndsWith(".template.template.xml"));
            string xml = ReadTarEntry(ms, entryName)!;

            // Structural HTML (siteWrapper div) should be present
            Assert.Contains("siteWrapper", xml);
            // DNN directives should have been removed
            Assert.DoesNotContain("<%@", xml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithThemesZip_ManifestIncludesTemplateRow()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);
            string manifest = ReadTarEntry(ms, "manifest.csv")!;

            Assert.Contains("INCLUDED,template,", manifest);
        }
        finally { File.Delete(zipPath); }
    }

    // ------------------------------------------------------------------
    // ASCX → HTML conversion tests
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertAscxToContainerHtml_RemovesDirectives()
    {
        const string ascx = """
            <%@ Control AutoEventWireup="false" Inherits="DotNetNuke.UI.Containers.Container" %>
            <%@ Register TagPrefix="dnn" TagName="TITLE" Src="~/Admin/Containers/Title.ascx" %>
            <div class="container"></div>
            """;

        string result = BundleWriter.ConvertAscxToContainerHtml(ascx);

        Assert.DoesNotContain("<%@", result);
    }

    [Fact]
    public void ConvertAscxToContainerHtml_ReplacesTitleControl()
    {
        const string ascx = """
            <div>
                <h2><dnn:TITLE runat="server" id="dnnTITLE" CssClass="Title" /></h2>
                <div id="ContentPane" runat="server"></div>
            </div>
            """;

        string result = BundleWriter.ConvertAscxToContainerHtml(ascx);

        Assert.Contains("$dotContent.title", result);
        Assert.DoesNotContain("dnn:TITLE", result);
    }

    [Fact]
    public void ConvertAscxToContainerHtml_ReplacesContentPane()
    {
        const string ascx = """
            <div class="wrapper">
                <div id="ContentPane" runat="server"></div>
            </div>
            """;

        string result = BundleWriter.ConvertAscxToContainerHtml(ascx);

        Assert.Contains("$!{dotContent.body}", result);
        Assert.DoesNotContain("ContentPane", result);
    }

    [Fact]
    public void ConvertAscxToContainerHtml_RemovesRunatServer()
    {
        const string ascx = """<div id="Wrapper" runat="server"><p>text</p></div>""";

        string result = BundleWriter.ConvertAscxToContainerHtml(ascx);

        Assert.DoesNotContain("runat=\"server\"", result);
        Assert.Contains("<div id=\"Wrapper\">", result);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_RemovesDirectives()
    {
        const string ascx = """
            <%@ Control Language="vb" Inherits="DotNetNuke.UI.Skins.Skin" %>
            <%@ Register TagPrefix="dnn" TagName="LOGO" Src="~/Admin/Skins/Logo.ascx" %>
            <div id="siteWrapper"></div>
            """;

        string result = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.DoesNotContain("<%@", result);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_ReplacesLogoControl()
    {
        const string ascx = """
            <div><dnn:LOGO runat="server" id="dnnLogo" /></div>
            """;

        string result = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains("<img", result);
        Assert.DoesNotContain("dnn:LOGO", result);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_ReplacesMenuControl()
    {
        const string ascx = """
            <nav><dnn:MENU runat="server" MenuStyle="Suckerfish" /></nav>
            """;

        string result = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains("<!-- Navigation -->", result);
        Assert.DoesNotContain("dnn:MENU", result);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_RemovesStylesAndJquery()
    {
        const string ascx = """
            <dnn:STYLES runat="server" id="styles" />
            <dnn:jQuery runat="server" id="jquery" />
            <div id="body"></div>
            """;

        string result = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.DoesNotContain("dnn:STYLES", result);
        Assert.DoesNotContain("dnn:jQuery", result);
        Assert.Contains("id=\"body\"", result);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_PreservesStructuralHtml()
    {
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div id="siteWrapper">
                <div id="header"><dnn:LOGO runat="server" id="dnnLogo" /></div>
                <div id="ContentPane" runat="server"></div>
                <div id="footer"><!-- footer --></div>
            </div>
            """;

        string result = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains("id=\"siteWrapper\"", result);
        Assert.Contains("id=\"header\"", result);
        Assert.Contains("id=\"footer\"", result);
    }

    // ------------------------------------------------------------------
    // Sub-folder ASCX files are excluded from conversion
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithThemesZip_SubFolderAscxIsNotConvertedToEntry()
    {
        // Build a themes zip that contains a sub-folder ASCX (Common/AddFiles.ascx)
        // — these should be skipped; only top-level ThemeName/*.ascx are converted.
        string path = Path.GetTempFileName() + ".zip";
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                // Top-level container — should produce an entry
                ZipArchiveEntry top = zip.CreateEntry(
                    "_default/Containers/TestTheme/Boxed.ascx");
                using (var w = new StreamWriter(top.Open()))
                    w.Write("""<div id="ContentPane" runat="server"></div>""");

                // Sub-folder helper — should be skipped
                ZipArchiveEntry sub = zip.CreateEntry(
                    "_default/Skins/TestTheme/Common/AddFiles.ascx");
                using (var w = new StreamWriter(sub.Open()))
                    w.Write("""<div>helper</div>""");
            }

            var (_, names) = WriteBundleToMemory([MakeHtmlContentType()], path);

            int containerCount = names.Count(n => n.EndsWith(".containers.container.xml"));
            int templateCount  = names.Count(n => n.EndsWith(".template.template.xml"));

            Assert.Equal(1, containerCount);  // only the top-level container
            Assert.Equal(0, templateCount);   // no top-level skins
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------
    // Manifest format — inode column for containers and templates
    // ------------------------------------------------------------------

    [Fact]
    public void Manifest_ContainerRow_HasInodeInFourthColumn()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (ms, _) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);
            string manifest = ReadTarEntry(ms, "manifest.csv")!;

            // Find the containers row and verify the 4th CSV column (inode) is a UUID.
            string containerLine = manifest
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .First(l => l.StartsWith("INCLUDED,containers,"));

            string[] cols = containerLine.Split(',');
            Assert.True(cols.Length >= 4, "Container row should have at least 4 columns.");
            Assert.True(Guid.TryParse(cols[3], out _),
                $"Inode column (col 4) should be a UUID; got: '{cols[3]}'");
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Manifest_TemplateRow_HasInodeInFourthColumn()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (ms, _) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);
            string manifest = ReadTarEntry(ms, "manifest.csv")!;

            string templateLine = manifest
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .First(l => l.StartsWith("INCLUDED,template,"));

            string[] cols = templateLine.Split(',');
            Assert.True(cols.Length >= 4, "Template row should have at least 4 columns.");
            Assert.True(Guid.TryParse(cols[3], out _),
                $"Inode column (col 4) should be a UUID; got: '{cols[3]}'");
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Manifest_ContainerRow_HasEmptySiteAndFolder()
    {
        // DB-type containers use empty site/folder in the manifest;
        // DotCMS scans working/ subdirectories and reads <hostId> from the XML.
        string zipPath = BuildThemesZip();
        try
        {
            var (ms, _) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);
            string manifest = ReadTarEntry(ms, "manifest.csv")!;

            string containerLine = manifest
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .First(l => l.StartsWith("INCLUDED,containers,"));

            // columns: INCLUDED,containers,{id},{inode},{name},{site},{folder},,reason
            string[] cols = containerLine.Split(',');
            Assert.True(cols.Length >= 7, "Container row should have at least 7 columns.");
            Assert.Equal(string.Empty, cols[5]); // site
            Assert.Equal(string.Empty, cols[6]); // folder
        }
        finally { File.Delete(zipPath); }
    }

    // ------------------------------------------------------------------
    // Site / host entry tests
    // ------------------------------------------------------------------

    private static (MemoryStream stream, List<string> entryNames) WriteBundleWithSite(
        string siteName,
        string? themesZipPath = null)
    {
        var ms = new MemoryStream();
        BundleWriter.Write([MakeHtmlContentType()], ms, themesZipPath, siteName);
        ms.Position = 0;

        var names = new List<string>();
        using var gz  = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gz);

        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
            names.Add(entry.Name);

        ms.Position = 0;
        return (ms, names);
    }

    [Fact]
    public void Write_WithSiteName_IncludesHostXmlEntry()
    {
        var (_, names) = WriteBundleWithSite("My Website");

        Assert.Contains(names, n =>
            n.StartsWith("live/System Host/1/") &&
            n.EndsWith(".content.host.xml"));
    }

    [Fact]
    public void Write_WithSiteName_HostXmlHasCorrectRootElement()
    {
        var (ms, names) = WriteBundleWithSite("My Website");
        string entryName = names.First(n => n.EndsWith(".content.host.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("<com.dotcms.publisher.pusher.wrapper.HostWrapper>", xml);
        Assert.Contains("<operation>PUBLISH</operation>", xml);
        Assert.Contains("<assetType>contentlet</assetType>", xml);
    }

    [Fact]
    public void Write_WithSiteName_HostXmlContainsHostname()
    {
        var (ms, names) = WriteBundleWithSite("My Website");
        string xml = ReadTarEntry(ms, names.First(n => n.EndsWith(".content.host.xml")))!;

        // "My Website" should be sanitized to "my-website"
        Assert.Contains("my-website", xml);
        Assert.Contains("<string>hostname</string>", xml);
    }

    [Fact]
    public void Write_WithSiteName_ManifestIncludesHostRow()
    {
        var (ms, _) = WriteBundleWithSite("My Website");
        string manifest = ReadTarEntry(ms, "manifest.csv")!;

        Assert.Contains("INCLUDED,host,", manifest);
        Assert.Contains("my-website", manifest);
    }

    [Fact]
    public void Write_WithSiteName_ContainersGoToSiteDirectory()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (_, names) = WriteBundleWithSite("My Website", zipPath);

            // Containers should be in the sanitized site directory, not System Host.
            Assert.Contains(names, n =>
                n.StartsWith("live/my-website/") &&
                n.EndsWith(".containers.container.xml"));
            Assert.DoesNotContain(names, n =>
                n.StartsWith("working/System Host/") &&
                n.EndsWith(".containers.container.xml"));
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithSiteName_TemplatesGoToSiteDirectory()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (_, names) = WriteBundleWithSite("My Website", zipPath);

            Assert.Contains(names, n =>
                n.StartsWith("live/my-website/") &&
                n.EndsWith(".template.template.xml"));
            Assert.DoesNotContain(names, n =>
                n.StartsWith("working/System Host/") &&
                n.EndsWith(".template.template.xml"));
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithSiteName_ContainerXmlHasSiteHostId()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleWithSite("My Website", zipPath);
            string entryName = names.First(n => n.EndsWith(".containers.container.xml"));
            string xml = ReadTarEntry(ms, entryName)!;

            // The <hostId> inside the XML should NOT be the literal SYSTEM_HOST;
            // it must be the site's UUID.
            Assert.DoesNotContain("<hostId>SYSTEM_HOST</hostId>", xml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithoutSiteName_NoHostXmlEntry()
    {
        var (_, names) = WriteBundleToMemory([MakeHtmlContentType()]);

        Assert.DoesNotContain(names, n => n.EndsWith(".content.host.xml"));
    }

    [Fact]
    public void Write_WithoutSiteName_ContainersStayOnSystemHost()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (_, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);

            Assert.Contains(names, n =>
                n.StartsWith("live/System Host/") &&
                n.EndsWith(".containers.container.xml"));
        }
        finally { File.Delete(zipPath); }
    }

    // ------------------------------------------------------------------
    // Site-scoped content type tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithSiteName_ContentTypesGoToSiteDirectory()
    {
        var (_, names) = WriteBundleWithSite("My Website");

        Assert.Contains(names, n =>
            n.StartsWith("working/my-website/") &&
            n.EndsWith(".contentType.json"));
        Assert.DoesNotContain(names, n =>
            n.StartsWith("working/System Host/") &&
            n.EndsWith(".contentType.json"));
    }

    [Fact]
    public void Write_WithSiteName_ContentTypeJsonHasSiteHostId()
    {
        var (ms, names) = WriteBundleWithSite("My Website");
        string entryName = names.First(n => n.EndsWith(".contentType.json"));
        string json = ReadTarEntry(ms, entryName)!;
        using var doc = JsonDocument.Parse(json);

        string host = doc.RootElement
            .GetProperty("contentType").GetProperty("host").GetString()!;

        // Must NOT be the literal SYSTEM_HOST value; must be a site UUID.
        Assert.NotEqual("SYSTEM_HOST", host);
        Assert.True(Guid.TryParse(host, out _),
            $"host should be a UUID; got: '{host}'");
    }

    [Fact]
    public void Write_WithSiteName_ContentTypeJsonHasSiteName()
    {
        var (ms, names) = WriteBundleWithSite("My Website");
        string entryName = names.First(n => n.EndsWith(".contentType.json"));
        string json = ReadTarEntry(ms, entryName)!;
        using var doc = JsonDocument.Parse(json);

        string siteName = doc.RootElement
            .GetProperty("contentType").GetProperty("siteName").GetString()!;

        Assert.Equal("my-website", siteName);
    }

    [Fact]
    public void Write_WithSiteName_ManifestContentTypeRowUsesSiteName()
    {
        var (ms, _) = WriteBundleWithSite("My Website");
        string manifest = ReadTarEntry(ms, "manifest.csv")!;

        string ctLine = manifest
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(l => l.StartsWith("INCLUDED,contenttype,"));

        // The site column should be "my-website", not "System Host".
        Assert.Contains("my-website", ctLine);
        Assert.DoesNotContain("System Host", ctLine);
    }

    [Fact]
    public void Write_WithoutSiteName_ContentTypesStayOnSystemHost()
    {
        var (_, names) = WriteBundleToMemory([MakeHtmlContentType()]);

        Assert.Contains(names, n =>
            n.StartsWith("working/System Host/") &&
            n.EndsWith(".contentType.json"));
        Assert.DoesNotContain(names, n =>
            !n.StartsWith("working/System Host/") &&
            n.EndsWith(".contentType.json"));
    }

    [Fact]
    public void Write_WithoutSiteName_ContentTypeJsonHasSystemHost()
    {
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        using var doc = ParseFirstContentTypeJson(ms, names);

        JsonElement ct = doc.RootElement.GetProperty("contentType");
        Assert.Equal("SYSTEM_HOST", ct.GetProperty("host").GetString());
        Assert.Equal("systemHost",  ct.GetProperty("siteName").GetString());
    }

    // ------------------------------------------------------------------
    // SanitizeHostname utility tests
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("My Website",        "my-website")]
    [InlineData("DNN Site Export",   "dnn-site-export")]
    [InlineData("Hello World!",      "hello-world")]
    [InlineData("  spaces  ",        "spaces")]
    [InlineData("A",                 "a")]
    [InlineData("",                  "imported-site")]
    [InlineData("---",               "imported-site")]
    public void SanitizeHostname_ProducesExpectedResult(string input, string expected)
    {
        Assert.Equal(expected, BundleWriter.SanitizeHostname(input));
    }

    // ------------------------------------------------------------------
    // Contentlet (HTML content) bundle entry tests
    // ------------------------------------------------------------------

    private static IReadOnlyList<DnnHtmlContent> MakeHtmlContents() =>
    [
        new DnnHtmlContent("Home Banner", "<h1>Welcome</h1><p>Hello world!</p>"),
        new DnnHtmlContent("About Us",    "<p>We are a great company.</p>"),
    ];

    private static (MemoryStream stream, List<string> entryNames) WriteBundleWithContents(
        IReadOnlyList<DnnHtmlContent> htmlContents,
        string? siteName = null)
    {
        var ms = new MemoryStream();
        BundleWriter.Write([MakeHtmlContentType()], ms,
            themesZipPath: null, siteName: siteName, htmlContents: htmlContents);
        ms.Position = 0;

        var names = new List<string>();
        using var gz  = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gz);

        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
            names.Add(entry.Name);

        ms.Position = 0;
        return (ms, names);
    }

    [Fact]
    public void Write_WithHtmlContents_IncludesContentXmlEntries()
    {
        var (_, names) = WriteBundleWithContents(MakeHtmlContents());

        // Each HTML content item should have a .content.xml file under live/.../1/
        int count = names.Count(n =>
            n.Contains("/1/") && n.EndsWith(".content.xml"));
        Assert.Equal(2, count);
    }

    [Fact]
    public void Write_WithHtmlContents_ContentXmlUnderLiveDirectory()
    {
        var (_, names) = WriteBundleWithContents(MakeHtmlContents());

        Assert.All(
            names.Where(n => n.EndsWith(".content.xml") && n.Contains("/1/")),
            n => Assert.StartsWith("live/", n));
    }

    [Fact]
    public void Write_WithHtmlContents_IncludesWorkflowXmlEntries()
    {
        var (_, names) = WriteBundleWithContents(MakeHtmlContents());

        int count = names.Count(n =>
            n.Contains("/1/") && n.EndsWith(".contentworkflow.xml"));
        Assert.Equal(2, count);
    }

    [Fact]
    public void Write_WithHtmlContents_ContentXmlHasPushContentWrapper()
    {
        var (ms, names) = WriteBundleWithContents(MakeHtmlContents());
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("<com.dotcms.publisher.pusher.wrapper.PushContentWrapper>", xml);
        Assert.Contains("<operation>PUBLISH</operation>", xml);
    }

    [Fact]
    public void Write_WithHtmlContents_ContentXmlHasLiveAndWorkingInode()
    {
        var (ms, names) = WriteBundleWithContents(MakeHtmlContents());
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("<liveInode>", xml);
        Assert.Contains("<workingInode>", xml);
        Assert.Contains("<deleted>false</deleted>", xml);
    }

    [Fact]
    public void Write_WithHtmlContents_ContentXmlContainsTitleAndBody()
    {
        var contents = new[] { new DnnHtmlContent("My Title", "<p>My body</p>") };
        var (ms, names) = WriteBundleWithContents(contents);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("My Title", xml);
        Assert.Contains("&lt;p&gt;My body&lt;/p&gt;", xml); // XML-escaped
    }

    [Fact]
    public void Write_WithHtmlContents_ContentXmlReferencesHtmlContentTypeId()
    {
        var contents = new[] { new DnnHtmlContent("Title", "<p>Body</p>") };
        var (ms, names) = WriteBundleWithContents(contents);

        // Find the content type UUID used in the contentType.json
        string ctEntry = names.First(n => n.EndsWith(".contentType.json"));
        string ctJson  = ReadTarEntry(ms, ctEntry)!;
        using var ctDoc = JsonDocument.Parse(ctJson);
        string typeId = ctDoc.RootElement
            .GetProperty("contentType").GetProperty("id").GetString()!;

        // The contentlet XML should reference that same UUID
        string contentEntry = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml"));
        string contentXml   = ReadTarEntry(ms, contentEntry)!;
        Assert.Contains(typeId, contentXml);
    }

    [Fact]
    public void Write_WithHtmlContents_ContentXmlAssetSubTypeIsContentTypeVariable()
    {
        // assetSubType must match the content type variable (e.g. "htmlContent"), not the name
        // ("HTMLContent").  DotCMS uses assetSubType to resolve the content type on import.
        var contents = new[] { new DnnHtmlContent("Title", "<p>Body</p>") };
        var (ms, names) = WriteBundleWithContents(contents);
        string contentEntry = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml"));
        string contentXml   = ReadTarEntry(ms, contentEntry)!;

        Assert.Contains("<assetSubType>htmlContent</assetSubType>", contentXml);
        Assert.DoesNotContain("<assetSubType>HTMLContent</assetSubType>", contentXml);
    }

    [Fact]
    public void Write_WithHtmlContents_ContentXmlAssetNameIsIdentifierDotContent()
    {
        // assetName must follow the "{identifier}.content" convention used by DotCMS bundles.
        var contents = new[] { new DnnHtmlContent("Title", "<p>Body</p>") };
        var (ms, names) = WriteBundleWithContents(contents);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml"));
        string contentXml = ReadTarEntry(ms, entryName)!;

        // The entry name is "live/.../1/{identifier}.content.xml"; extract the identifier part.
        string entryBaseName = System.IO.Path.GetFileNameWithoutExtension(
            System.IO.Path.GetFileNameWithoutExtension(entryName)); // strips .xml then .content
        Assert.Contains($"<assetName>{entryBaseName}.content</assetName>", contentXml);
    }

    [Fact]
    public void Write_ContentTypeJson_SystemActionMappingsIsJsonArray()
    {
        // systemActionMappings must be a JSON array ("[]"), not a JSON object ("{}").
        // DotCMS expects a List serialization; an object causes deserialization failure on import.
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        string ctEntry = names.First(n => n.EndsWith(".contentType.json"));
        string ctJson  = ReadTarEntry(ms, ctEntry)!;

        using var doc = JsonDocument.Parse(ctJson);
        JsonElement mappings = doc.RootElement.GetProperty("systemActionMappings");
        Assert.Equal(JsonValueKind.Array, mappings.ValueKind);
    }

    [Fact]
    public void Write_WithHtmlContents_WorkflowXmlHasPushWorkflowWrapper()
    {
        var (ms, names) = WriteBundleWithContents(MakeHtmlContents());
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".contentworkflow.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("<com.dotcms.publisher.pusher.wrapper.PushContentWorkflowWrapper>", xml);
        Assert.Contains("<webasset>", xml);
    }

    [Fact]
    public void Write_WithHtmlContents_ManifestIncludesContentletRows()
    {
        var (ms, _) = WriteBundleWithContents(MakeHtmlContents());
        string manifest = ReadTarEntry(ms, "manifest.csv")!;

        int count = manifest.Split('\n')
            .Count(l => l.StartsWith("INCLUDED,contentlet,"));
        Assert.Equal(2, count);
    }

    [Fact]
    public void Write_WithNoHtmlContentType_HtmlContentsSkipped()
    {
        // If the content types don't include htmlContent, no contentlets should be written.
        var otherType = new DotCmsContentType { Name = "Event", Variable = "event", Fields = [] };
        var htmlContents = MakeHtmlContents();
        var ms = new MemoryStream();
        BundleWriter.Write([otherType], ms, themesZipPath: null,
            siteName: null, htmlContents: htmlContents);
        ms.Position = 0;

        var names = new List<string>();
        using var gz  = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gz);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
            names.Add(entry.Name);

        Assert.DoesNotContain(names, n => n.EndsWith(".content.xml") && n.Contains("/1/"));
    }

    [Fact]
    public void Write_WithSiteName_ContentletsGoToSiteDirectory()
    {
        var (_, names) = WriteBundleWithContents(MakeHtmlContents(), siteName: "My Website");

        Assert.Contains(names, n =>
            n.StartsWith("live/my-website/1/") &&
            n.EndsWith(".content.xml"));
        Assert.DoesNotContain(names, n =>
            n.StartsWith("live/System Host/1/") &&
            n.EndsWith(".content.xml"));
    }

    // ------------------------------------------------------------------
    // Container/template published-state (live/) tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithThemesZip_ContainerXmlHasLiveInode()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);
            string entryName = names.First(n => n.EndsWith(".containers.container.xml"));
            string xml = ReadTarEntry(ms, entryName)!;

            Assert.Contains("<liveInode>", xml);
            Assert.Contains("<workingInode>", xml);
            Assert.Contains("<deleted>false</deleted>", xml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithThemesZip_TemplateXmlHasLiveInode()
    {
        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);
            string entryName = names.First(n => n.EndsWith(".template.template.xml"));
            string xml = ReadTarEntry(ms, entryName)!;

            Assert.Contains("<liveInode>", xml);
            Assert.Contains("<workingInode>", xml);
            Assert.Contains("<deleted>false</deleted>", xml);
        }
        finally { File.Delete(zipPath); }
    }

    // ------------------------------------------------------------------
    // VARCHAR(255) truncation tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_ContentTypeDescription_TruncatedTo255InJson()
    {
        // A content type whose description exceeds 255 characters should have
        // it silently trimmed so dotCMS can store it in its VARCHAR(255) column.
        var ct = MakeHtmlContentType();
        ct.Description = new string('d', 300);

        var (ms, names) = WriteBundleToMemory([ct]);
        string entryName = names.First(n => n.EndsWith(".contentType.json"));
        string json = ReadTarEntry(ms, entryName)!;

        using var doc = JsonDocument.Parse(json);
        string? description = doc.RootElement
            .GetProperty("contentType")
            .GetProperty("description")
            .GetString();

        Assert.NotNull(description);
        Assert.True(description.Length <= 255,
            $"description length {description.Length} exceeds 255.");
        Assert.Equal(255, description.Length);
    }

    [Fact]
    public void Write_HtmlContentTitle_TruncatedTo255InContentXml()
    {
        // A DNN HTML content item whose title exceeds 255 characters should be
        // stored with a truncated title so dotCMS does not reject the import.
        string longTitle = new string('t', 300);
        var contents = new[] { new DnnHtmlContent(longTitle, "<p>body</p>") };

        var (ms, names) = WriteBundleWithContents(contents);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        // The title in the XML must not contain the full 300-character string.
        Assert.DoesNotContain(longTitle, xml);
        // The truncated 255-char version must be present.
        Assert.Contains(new string('t', 255), xml);
    }

    [Fact]
    public void Write_HtmlContentTitle_TruncatedTo255InWorkflowXml()
    {
        string longTitle = new string('w', 300);
        var contents = new[] { new DnnHtmlContent(longTitle, "<p>body</p>") };

        var (ms, names) = WriteBundleWithContents(contents);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".contentworkflow.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.DoesNotContain(longTitle, xml);
        Assert.Contains(new string('w', 255), xml);
    }

    [Fact]
    public void Write_ContainerTitle_TruncatedTo255InContainerXml()
    {
        // Build a themes zip whose container file name is deliberately very long.
        string longName = new string('c', 260);
        string path = Path.GetTempFileName() + ".zip";
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                ZipArchiveEntry e = zip.CreateEntry(
                    $"_default/Containers/TestTheme/{longName}.ascx");
                using var w = new StreamWriter(e.Open());
                w.Write("""<div id="ContentPane" runat="server"></div>""");
            }

            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], path);
            string entryName = names.First(n => n.EndsWith(".containers.container.xml"));
            string xml = ReadTarEntry(ms, entryName)!;

            // The <title> element must not exceed 255 characters.
            int start = xml.IndexOf("<title>", StringComparison.Ordinal) + "<title>".Length;
            int end   = xml.IndexOf("</title>", start, StringComparison.Ordinal);
            string titleContent = xml[start..end];

            Assert.True(titleContent.Length <= 255,
                $"Container title length {titleContent.Length} exceeds 255.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_TemplateTitle_TruncatedTo255InTemplateXml()
    {
        string longName = new string('s', 260);
        string path = Path.GetTempFileName() + ".zip";
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                ZipArchiveEntry e = zip.CreateEntry(
                    $"_default/Skins/TestTheme/{longName}.ascx");
                using var w = new StreamWriter(e.Open());
                w.Write("""<div id="siteWrapper"></div>""");
            }

            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], path);
            string entryName = names.First(n => n.EndsWith(".template.template.xml"));
            string xml = ReadTarEntry(ms, entryName)!;

            int start = xml.IndexOf("<title>", StringComparison.Ordinal) + "<title>".Length;
            int end   = xml.IndexOf("</title>", start, StringComparison.Ordinal);
            string titleContent = xml[start..end];

            Assert.True(titleContent.Length <= 255,
                $"Template title length {titleContent.Length} exceeds 255.");
        }
        finally { File.Delete(path); }
    }
}
