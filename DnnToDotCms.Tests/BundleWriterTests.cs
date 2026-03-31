using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
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

        // Each content type is written twice to the tar (DotCMS push-publish requirement),
        // so count unique file names.
        int count = names
            .Where(n => n.StartsWith("working/System Host/") && n.EndsWith(".contentType.json"))
            .Distinct()
            .Count();

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
        return ParseContentTypeJsonFromEntry(ms, entryName);
    }

    /// <summary>
    /// Reads a <c>.contentType.json</c> tar entry and returns only the first
    /// JSON object.  The bundle format places two concatenated JSON objects in
    /// each file; <see cref="JsonDocument.Parse"/> would reject the second one.
    /// </summary>
    private static JsonDocument ParseContentTypeJsonFromEntry(MemoryStream ms, string entryName)
    {
        string json  = ReadTarEntry(ms, entryName)!;
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var reader   = new System.Text.Json.Utf8JsonReader(bytes);
        return JsonDocument.ParseValue(ref reader);
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
            // Each content type is written twice; count unique file names.
            int ctCount = names
                .Where(n => n.StartsWith("working/System Host/") && n.EndsWith(".contentType.json"))
                .Distinct()
                .Count();
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

        // "My Website" should be sanitized to "My_Website"
        Assert.Contains("My_Website", xml);
        Assert.Contains("<string>hostname</string>", xml);
    }

    [Fact]
    public void Write_WithSiteName_ManifestIncludesHostRow()
    {
        var (ms, _) = WriteBundleWithSite("My Website");
        string manifest = ReadTarEntry(ms, "manifest.csv")!;

        Assert.Contains("INCLUDED,host,", manifest);
        Assert.Contains("My_Website", manifest);
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
                n.StartsWith("live/My_Website/") &&
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
                n.StartsWith("live/My_Website/") &&
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
            n.StartsWith("working/My_Website/") &&
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
        using var doc = ParseContentTypeJsonFromEntry(ms, entryName);

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
        using var doc = ParseContentTypeJsonFromEntry(ms, entryName);

        string siteName = doc.RootElement
            .GetProperty("contentType").GetProperty("siteName").GetString()!;

        Assert.Equal("My_Website", siteName);
    }

    [Fact]
    public void Write_WithSiteName_ManifestContentTypeRowUsesSiteName()
    {
        var (ms, _) = WriteBundleWithSite("My Website");
        string manifest = ReadTarEntry(ms, "manifest.csv")!;

        string ctLine = manifest
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(l => l.StartsWith("INCLUDED,contenttype,"));

        // The site column should be "My_Website", not "System Host".
        Assert.Contains("My_Website", ctLine);
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
    [InlineData("My Website",        "My_Website")]
    [InlineData("DNN Site Export",   "DNN_Site_Export")]
    [InlineData("Hello World!",      "Hello_World")]
    [InlineData("  spaces  ",        "spaces")]
    [InlineData("A",                 "A")]
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
    public void Write_WithHtmlContents_ContentXmlHasMultiTreeElement()
    {
        // wrapperMultiTree must not be null on import — DotCMS calls .isEmpty() on it and
        // throws a NullPointerException when the element is absent from the XML.
        var (ms, names) = WriteBundleWithContents(MakeHtmlContents());
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("<multiTree/>", xml);
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
        using var ctDoc = ParseContentTypeJsonFromEntry(ms, ctEntry);
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
        using var doc = ParseContentTypeJsonFromEntry(ms, ctEntry);
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
    public void Write_WithHtmlContents_WorkflowXmlHasNoAssignedTo()
    {
        // workflow_task.assigned_to has a FK constraint referencing cms_role(id).
        // User IDs (e.g. "dotcms.org.1") are not valid cms_role IDs and cause a
        // FK violation on bundle import.  The element must be absent so the DB
        // column is left NULL, which satisfies the nullable FK column.
        var (ms, names) = WriteBundleWithContents(MakeHtmlContents());
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".contentworkflow.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.DoesNotContain("<assignedTo>", xml);
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
    public void Write_WithHtmlContents_ContentXmlFolderIsSystemFolder()
    {
        // The folder field must be "SYSTEM_FOLDER" so that DotCMS uses the site
        // as the parent when creating identifiers (avoids the
        // "You can only create an identifier on a host of folder. Trying null" error).
        var contents = new[] { new DnnHtmlContent("Title", "<p>Body</p>") };
        var (ms, names) = WriteBundleWithContents(contents);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml"));
        string xml = ReadTarEntry(ms, entryName)!;
        string folderValue = ExtractXmlStringField(xml, "folder");

        Assert.Equal("SYSTEM_FOLDER", folderValue);
    }

    /// <summary>
    /// Extracts the string value immediately following a <c>&lt;string&gt;{key}&lt;/string&gt;</c>
    /// element in a DotCMS XStream-serialised XML blob.
    /// </summary>
    private static string ExtractXmlStringField(string xml, string key)
    {
        int keyIdx = xml.IndexOf($"<string>{key}</string>", StringComparison.Ordinal);
        Assert.True(keyIdx >= 0, $"Key '{key}' not found in XML");
        int valueStart = xml.IndexOf("<string>", keyIdx + 1, StringComparison.Ordinal) + "<string>".Length;
        int valueEnd   = xml.IndexOf("</string>", valueStart, StringComparison.Ordinal);
        return xml[valueStart..valueEnd];
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
            n.StartsWith("live/My_Website/1/") &&
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
        using var doc = ParseContentTypeJsonFromEntry(ms, entryName);
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

    // ------------------------------------------------------------------
    // Content type JSON duplication tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_ContentTypeJson_FileAppearsTwiceInTar()
    {
        // The DotCMS push-publish format requires each .contentType.json to be
        // written twice to the tar archive.
        var (_, names) = WriteBundleToMemory([MakeHtmlContentType()]);

        int totalCount = names.Count(n =>
            n.StartsWith("working/") && n.EndsWith(".contentType.json"));

        Assert.Equal(2, totalCount); // one content type → two tar entries
    }

    [Fact]
    public void Write_ContentTypeJson_FileContainsTwoJsonObjects()
    {
        // Each .contentType.json entry must contain two consecutive JSON objects
        // (old state + new state) concatenated without a separator.
        var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()]);
        string entryName = names.First(n => n.EndsWith(".contentType.json"));
        string json = ReadTarEntry(ms, entryName)!;

        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var objects = new List<object?>();
        int pos = 0;

        // Count how many top-level JSON values can be parsed from the content.
        while (pos < bytes.Length)
        {
            var r = new System.Text.Json.Utf8JsonReader(bytes.AsSpan(pos));
            try
            {
                using var doc = JsonDocument.ParseValue(ref r);
                objects.Add(null); // successfully parsed one object
                pos += (int)r.BytesConsumed;
                // skip any whitespace
                while (pos < bytes.Length && bytes[pos] <= 0x20) pos++;
            }
            catch (System.Text.Json.JsonException)
            {
                break;
            }
        }

        Assert.Equal(2, objects.Count);
    }

    // ------------------------------------------------------------------
    // Portal pages (htmlpageasset) tests
    // ------------------------------------------------------------------

    private static (MemoryStream stream, List<string> entryNames) WriteBundleWithPages(
        IReadOnlyList<DnnPortalPage> pages,
        string siteName = "Test Site")
    {
        var ms = new MemoryStream();
        BundleWriter.Write([MakeHtmlContentType()], ms, null, siteName, null, pages);
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
    public void Write_WithPages_HomepageProducesHtmlpageassetEntry()
    {
        var pages = new[]
        {
            new DnnPortalPage("78f7202a-f8d1-4b13-9682-583f6afb10ea",
                "Home", "Home", "", "//Home", 0, true, "[G]Skins/Xcillion/Home.ascx"),
        };

        var (_, names) = WriteBundleWithPages(pages);

        Assert.Contains(names, n => n.Contains("/1/") && n.EndsWith(".content.xml"));
    }

    [Fact]
    public void Write_WithPages_HomepageContentXmlHasHtmlpageassetSubType()
    {
        var pages = new[]
        {
            new DnnPortalPage("78f7202a-f8d1-4b13-9682-583f6afb10ea",
                "Home", "Home", "", "//Home", 0, true, ""),
        };

        var (ms, names) = WriteBundleWithPages(pages);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("<assetSubType>htmlpageasset</assetSubType>", xml);
    }

    [Fact]
    public void Write_WithPages_HomepageContentXmlHasTitle()
    {
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Home", "My Home Page", "", "//Home", 0, true, ""),
        };

        var (ms, names) = WriteBundleWithPages(pages);
        // Only page content XML (not host)
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("My Home Page", xml);
    }

    [Fact]
    public void Write_WithPages_AdminPageIsExcluded()
    {
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Home",  "Home",  "", "//Home",  0, true, ""),
            new DnnPortalPage("bbb", "Admin", "Admin", "", "//Admin", 0, false, ""),
        };

        var (_, names) = WriteBundleWithPages(pages);

        // Exactly one page content XML (excluding host entry).
        int pageCount = names.Count(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml") && !n.Contains("htmlContent"));
        Assert.Equal(1, pageCount);
    }

    [Fact]
    public void Write_WithPages_SubPagesAreExcluded()
    {
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Home",       "Home",       "", "//Home",           0, true, ""),
            new DnnPortalPage("bbb", "My Profile", "My Profile", "", "//Activity//MyProfile", 1, true, ""),
        };

        var (_, names) = WriteBundleWithPages(pages);

        // Only the Level-0 Home page should produce a content.xml entry.
        int pageCount = names.Count(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml") && !n.Contains("htmlContent"));
        Assert.Equal(1, pageCount);
    }

    [Fact]
    public void Write_WithPages_ProducesWorkflowXmlEntry()
    {
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Home", "Home", "", "//Home", 0, true, ""),
        };

        var (_, names) = WriteBundleWithPages(pages);

        Assert.Contains(names, n => n.Contains("/1/") && n.EndsWith(".contentworkflow.xml"));
    }

    [Fact]
    public void Write_WithPages_ManifestIncludesContentletRows()
    {
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Home", "Home", "", "//Home", 0, true, ""),
        };

        var (ms, _) = WriteBundleWithPages(pages);
        string manifest = ReadTarEntry(ms, "manifest.csv")!;

        Assert.Contains("INCLUDED,contentlet,", manifest);
    }

    [Fact]
    public void Write_WithPages_PageContentXmlHasMultiTreeElement()
    {
        // wrapperMultiTree must not be null on import — DotCMS calls .isEmpty() on it and
        // throws a NullPointerException when the element is absent from the XML.
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Home", "Home", "", "//Home", 0, true, ""),
        };

        var (ms, names) = WriteBundleWithPages(pages);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("<multiTree/>", xml);
    }

    // ------------------------------------------------------------------
    // Portal static files (FileAsset) tests
    // ------------------------------------------------------------------

    private static (MemoryStream stream, List<string> entryNames) WriteBundleWithFiles(
        IReadOnlyList<DnnPortalFile> files,
        string siteName = "Test Site")
    {
        var ms = new MemoryStream();
        BundleWriter.Write([MakeHtmlContentType()], ms, null, siteName, null, null, files);
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
    public void Write_WithPortalFiles_ProducesAssetBinaryEntry()
    {
        var files = new[]
        {
            new DnnPortalFile(
                "e5dfe1f2-4cdc-46bd-ad32-7257a6b8105a",
                "2af85195-c192-4a33-a14d-a8bb2dc6007e",
                "home.css", "", "text/css",
                Encoding.UTF8.GetBytes("body { margin: 0; }")),
        };

        var (_, names) = WriteBundleWithFiles(files);

        // Binary should be under assets/{x}/{y}/{inode}/fileAsset/{filename}
        Assert.Contains(names, n => n.StartsWith("assets/") && n.EndsWith("/home.css"));
    }

    [Fact]
    public void Write_WithPortalFiles_ProducesContentXmlEntry()
    {
        var files = new[]
        {
            new DnnPortalFile(
                "e5dfe1f2-4cdc-46bd-ad32-7257a6b8105a",
                "2af85195-c192-4a33-a14d-a8bb2dc6007e",
                "home.css", "", "text/css",
                Encoding.UTF8.GetBytes("body {}")),
        };

        var (_, names) = WriteBundleWithFiles(files);

        Assert.Contains(names, n => n.Contains("/1/") && n.EndsWith(".content.xml"));
    }

    [Fact]
    public void Write_WithPortalFiles_ContentXmlHasFileAssetSubType()
    {
        var files = new[]
        {
            new DnnPortalFile(
                "e5dfe1f2-4cdc-46bd-ad32-7257a6b8105a",
                "2af85195-c192-4a33-a14d-a8bb2dc6007e",
                "logo.png", "Images/", "image/png",
                [0x89, 0x50, 0x4E, 0x47]),
        };

        var (ms, names) = WriteBundleWithFiles(files);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("<assetSubType>FileAsset</assetSubType>", xml);
    }

    [Fact]
    public void Write_WithPortalFiles_AssetPathUsesFirstTwoCharsOfInode()
    {
        // inode "2af85195-..." → assets/2/a/2af85195-.../fileAsset/home.css
        var files = new[]
        {
            new DnnPortalFile(
                "e5dfe1f2-4cdc-46bd-ad32-7257a6b8105a",
                "2af85195-c192-4a33-a14d-a8bb2dc6007e",
                "home.css", "", "text/css",
                Encoding.UTF8.GetBytes("/* css */")),
        };

        var (_, names) = WriteBundleWithFiles(files);

        Assert.Contains(names,
            n => n == "assets/2/a/2af85195-c192-4a33-a14d-a8bb2dc6007e/fileAsset/home.css");
    }

    [Fact]
    public void Write_WithPortalFiles_FileAssetContentXmlFolderIsSystemFolder()
    {
        // All file-asset contentlets must use SYSTEM_FOLDER so that DotCMS
        // falls back to the site as the identifier parent (avoids the
        // "You can only create an identifier on a host of folder. Trying null" error).
        var files = new[]
        {
            new DnnPortalFile(
                "6f574d5f-0880-4d5a-b4a2-74d2e10b5659",
                "69f363b0-6512-48ad-b187-b6a450ffda7b",
                "logo.png", "Images/", "image/png",
                [0x00]),
        };

        var (ms, names) = WriteBundleWithFiles(files, "Test Site");

        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml"));
        string xml = ReadTarEntry(ms, entryName)!;
        string folderValue = ExtractXmlStringField(xml, "folder");

        Assert.Equal("SYSTEM_FOLDER", folderValue);
    }

    [Fact]
    public void Write_WithPortalFiles_FileAssetContentXmlReferencesDataSharedPath()
    {
        var files = new[]
        {
            new DnnPortalFile(
                "e5dfe1f2-4cdc-46bd-ad32-7257a6b8105a",
                "2af85195-c192-4a33-a14d-a8bb2dc6007e",
                "home.css", "", "text/css",
                Encoding.UTF8.GetBytes("/* css */")),
        };

        var (ms, names) = WriteBundleWithFiles(files);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("/data/shared/assets/", xml);
        Assert.Contains("home.css", xml);
    }

    [Fact]
    public void Write_WithPortalFiles_FileAssetContentXmlHasMultiTreeElement()
    {
        // wrapperMultiTree must not be null on import — DotCMS calls .isEmpty() on it and
        // throws a NullPointerException when the element is absent from the XML.
        var files = new[]
        {
            new DnnPortalFile(
                "e5dfe1f2-4cdc-46bd-ad32-7257a6b8105a",
                "2af85195-c192-4a33-a14d-a8bb2dc6007e",
                "home.css", "", "text/css",
                Encoding.UTF8.GetBytes("/* css */")),
        };

        var (ms, names) = WriteBundleWithFiles(files);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("<multiTree/>", xml);
    }

    [Fact]
    public void Write_WithPortalFiles_RootFileParentPathIsSiteRoot()
    {
        // A portal file at the DNN root (FolderPath = "") should land at
        // parentPath="/", preserving the original folder structure in DotCMS.
        var files = new[]
        {
            new DnnPortalFile(
                "e5dfe1f2-4cdc-46bd-ad32-7257a6b8105a",
                "2af85195-c192-4a33-a14d-a8bb2dc6007e",
                "home.css", "", "text/css",
                Encoding.UTF8.GetBytes("/* css */")),
        };

        var (ms, names) = WriteBundleWithFiles(files);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("<parentPath>/</parentPath>", xml);
    }

    [Fact]
    public void Write_WithPortalFiles_SubFolderFileParentPathPreservesDnnFolder()
    {
        // A portal file in the DNN "Images/" folder should land at parentPath="/Images/"
        // so that the DotCMS folder structure mirrors the original DNN layout.
        var files = new[]
        {
            new DnnPortalFile(
                "6f574d5f-0880-4d5a-b4a2-74d2e10b5659",
                "69f363b0-6512-48ad-b187-b6a450ffda7b",
                "logo.png", "Images/", "image/png",
                [0x89, 0x50, 0x4E, 0x47]),
        };

        var (ms, names) = WriteBundleWithFiles(files);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("<parentPath>/Images/</parentPath>", xml);
        Assert.DoesNotContain("<parentPath>/</parentPath>", xml);
    }

    // ------------------------------------------------------------------
    // Page multiTree population tests
    // ------------------------------------------------------------------

    private static (MemoryStream stream, List<string> entryNames) WriteBundleWithPagesAndContents(
        IReadOnlyList<DnnPortalPage> pages,
        IReadOnlyList<DnnHtmlContent> htmlContents,
        string siteName = "Test Site",
        string? themesZipPath = null)
    {
        var ms = new MemoryStream();
        BundleWriter.Write([MakeHtmlContentType()], ms, themesZipPath, siteName,
            htmlContents, pages);
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
    public void Write_WithPageAndMatchingContent_PageXmlHasPopulatedMultiTree()
    {
        // When an HTML contentlet carries a TabUniqueId that matches a page,
        // the page's multiTree should contain a multiTree entry linking them.
        string tabId = "aaa-111-bbb";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "Home", "Home", "", "//Home", 0, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Welcome", "<h1>Hello</h1>", TabUniqueId: tabId),
        };

        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents,
                themesZipPath: zipPath);

            // Identify the page XML specifically by its assetSubType.
            string? pageXml = null;
            foreach (string name in names.Where(n =>
                n.Contains("/1/") && n.EndsWith(".content.xml") && !n.Contains("host.xml")))
            {
                string candidate = ReadTarEntry(ms, name)!;
                if (candidate.Contains("<assetSubType>htmlpageasset</assetSubType>"))
                {
                    pageXml = candidate;
                    break;
                }
            }

            Assert.NotNull(pageXml);

            // The multiTree should be populated, not self-closing.
            Assert.DoesNotContain("<multiTree/>", pageXml);
            Assert.Contains("<multiTree>", pageXml);
            // Each entry is serialized as a <map> with <entry> children (List<Map<String,Object>>).
            Assert.Contains("<map>", pageXml);
            Assert.Contains("<string>parent1</string>", pageXml);
            Assert.Contains("<string>parent2</string>", pageXml);
            Assert.Contains("<string>child</string>", pageXml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithPageAndNoMatchingContent_PageXmlHasEmptyMultiTree()
    {
        // When no HTML content matches the page's UniqueId, multiTree stays self-closing.
        var pages = new[]
        {
            new DnnPortalPage("page-uuid", "Home", "Home", "", "//Home", 0, true, ""),
        };
        var contents = new[]
        {
            // TabUniqueId does not match the page
            new DnnHtmlContent("Welcome", "<h1>Hello</h1>", TabUniqueId: "other-uuid"),
        };

        var (ms, names) = WriteBundleWithPagesAndContents(pages, contents);

        string? pageXml = null;
        foreach (string name in names.Where(n =>
            n.Contains("/1/") && n.EndsWith(".content.xml") && !n.Contains("host.xml")))
        {
            string candidate = ReadTarEntry(ms, name)!;
            if (candidate.Contains("<assetSubType>htmlpageasset</assetSubType>"))
            {
                pageXml = candidate;
                break;
            }
        }

        Assert.NotNull(pageXml);
        Assert.Contains("<multiTree/>", pageXml);
    }

    [Fact]
    public void Write_WithPageAndMatchingContent_MultiTreeReferencesContentIdentifier()
    {
        // The <child> element in the multiTree must reference the contentlet identifier
        // that was written to the bundle for the matching HTML content item.
        string tabId = "tab-xyz-123";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "Home", "Home", "", "//Home", 0, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Banner", "<p>content</p>", TabUniqueId: tabId),
        };

        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents,
                themesZipPath: zipPath);

            // Extract the contentlet identifier from the content.xml entry path.
            string contentEntry = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
                && !n.Contains("host.xml") && !n.Contains("htmlContent"));
            // Entries are named live/.../1/{identifier}.content.xml
            // Find the content entry that belongs to the HTML content (not the page).
            // Both the page and the contentlet are under /1/; find the contentlet by checking
            // that its XML contains the htmlContent assetSubType.
            string? contentletId = null;
            foreach (string name in names.Where(n => n.Contains("/1/") && n.EndsWith(".content.xml")
                                                     && !n.Contains("host.xml")))
            {
                string candidateXml = ReadTarEntry(ms, name)!;
                if (candidateXml.Contains("<assetSubType>htmlContent</assetSubType>"))
                {
                    // Extract the identifier from the XML.
                    int start = candidateXml.IndexOf("<assetName>", StringComparison.Ordinal) + "<assetName>".Length;
                    int end   = candidateXml.IndexOf(".content</assetName>", start, StringComparison.Ordinal);
                    contentletId = candidateXml[start..end];
                    break;
                }
            }

            Assert.NotNull(contentletId);

            // The page XML must reference that contentlet ID in its multiTree.
            string? pageXml = null;
            foreach (string name in names.Where(n => n.Contains("/1/") && n.EndsWith(".content.xml")
                                                     && !n.Contains("host.xml")))
            {
                string candidateXml = ReadTarEntry(ms, name)!;
                if (candidateXml.Contains("<assetSubType>htmlpageasset</assetSubType>"))
                {
                    pageXml = candidateXml;
                    break;
                }
            }

            Assert.NotNull(pageXml);
            // The child contentlet ID must appear inside the multiTree map entry.
            Assert.Contains($"<string>child</string>", pageXml);
            Assert.Contains($"<string>{contentletId}</string>", pageXml);
        }
        finally { File.Delete(zipPath); }
    }


}
