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
        Assert.Contains("SYSTEM_HOST", manifest);
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
                n => n.StartsWith("working/System Host/") &&
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
                n => n.StartsWith("working/System Host/") &&
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
}
