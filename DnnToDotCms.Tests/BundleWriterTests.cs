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
        while ((entry = tar.GetNextEntry(copyData: true)) is not null)
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
        while ((entry = tar.GetNextEntry(copyData: true)) is not null)
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
        Assert.NotNull(tar.GetNextEntry(copyData: true));
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
    public void ContentTypeJson_ContentType_NameHasNoLeadingOrTrailingSpaces()
    {
        // Content-type names must be trimmed; a name with surrounding whitespace
        // causes DotCMS to display the content type with leading spaces in the UI.
        var ct = MakeHtmlContentType();
        ct.Name = "  HTMLContent  ";   // deliberately add leading/trailing whitespace

        var (ms, names) = WriteBundleToMemory([ct]);
        using var doc = ParseFirstContentTypeJson(ms, names);

        string name = doc.RootElement
            .GetProperty("contentType").GetProperty("name").GetString()!;

        Assert.Equal("HTMLContent", name);
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
        while ((entry = tar.GetNextEntry(copyData: true)) is not null)
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

            // Include skin.css — DNN auto-loads this for every skin, and
            // real exports virtually always contain it.
            ZipArchiveEntry skinCss =
                zip.CreateEntry("_default/Skins/TestTheme/skin.css");
            using (var w = new StreamWriter(skinCss.Open()))
                w.Write("body{}");
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
        Assert.Contains("<div id=\"dnn_Wrapper\">", result);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_RemovesDirectives()
    {
        const string ascx = """
            <%@ Control Language="vb" Inherits="DotNetNuke.UI.Skins.Skin" %>
            <%@ Register TagPrefix="dnn" TagName="LOGO" Src="~/Admin/Skins/Logo.ascx" %>
            <div id="siteWrapper"></div>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.DoesNotContain("<%@", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_ReplacesLogoControl()
    {
        const string ascx = """
            <div><dnn:LOGO runat="server" id="dnnLogo" /></div>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains("<img", body);
        Assert.DoesNotContain("dnn:LOGO", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_ReplacesMenuControl()
    {
        const string ascx = """
            <nav><dnn:MENU runat="server" MenuStyle="Suckerfish" /></nav>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains("$navtool.getNav", body);
        Assert.Contains("#foreach", body);
        Assert.Contains("$navItem.showOnMenu", body);
        Assert.DoesNotContain("dnn:MENU", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_ReplacesNavControl()
    {
        const string ascx = """
            <div><dnn:NAV runat="server" /></div>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains("$navtool.getNav", body);
        Assert.DoesNotContain("dnn:NAV", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_MenuPreservesIdAttribute()
    {
        const string ascx = """
            <nav><dnn:MENU runat="server" id="dnnMENU" /></nav>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains(@"id=""dnnMENU""", body);
        Assert.Contains("$navtool.getNav", body);
        Assert.DoesNotContain("dnn:MENU", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_MenuPreservesCssClassAttribute()
    {
        const string ascx = """
            <dnn:MENU runat="server" id="topNav" CssClass="navbar-nav" />
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains(@"id=""topNav""", body);
        Assert.Contains(@"class=""navbar-nav""", body);
        Assert.Contains("$navtool.getNav", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_NavPreservesIdAttribute()
    {
        const string ascx = """
            <dnn:NAV runat="server" id="dnnNAV" CssClass="side-nav" />
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains(@"id=""dnnNAV""", body);
        Assert.Contains(@"class=""side-nav""", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_MenuWithNoIdOrClassProducesPlainUl()
    {
        const string ascx = """
            <dnn:MENU runat="server" MenuStyle="Suckerfish" />
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        // No id or CssClass → plain <ul> without attributes.
        Assert.Contains("<ul>", body);
        Assert.DoesNotContain(@"id=""", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_NavSnippetIncludesBootstrapNavClasses()
    {
        const string ascx = """
            <dnn:MENU runat="server" id="topNav" CssClass="navbar-nav" />
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        // Verify Bootstrap nav classes are present on nav items.
        Assert.Contains("nav-item", body);
        Assert.Contains("nav-link", body);
        Assert.Contains("text-expanded", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_NavSnippetIncludesDropdownSupport()
    {
        const string ascx = """
            <dnn:MENU runat="server" id="topNav" CssClass="navbar-nav" />
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        // Verify dropdown classes/structure are present for child nav items.
        Assert.Contains("dropdown", body);
        Assert.Contains("dropdown-toggle", body);
        Assert.Contains("dropdown-menu", body);
        Assert.Contains("$navItem.children", body);
        Assert.Contains("$childItem", body);
        Assert.Contains("caret", body);
    }

    // ------------------------------------------------------------------
    // ExpandMenuTemplateClasses tests
    // ------------------------------------------------------------------

    [Fact]
    public void ExpandMenuTemplateClasses_MergesTemplateClassesWithCssClass()
    {
        // Simulate a skin with <dnn:MENU MenuStyle="BootStrapNav" CssClass="w-100" />
        // and a BootStrapNav template whose root <ul> has additional classes.
        const string ascx =
            """<dnn:MENU runat="server" id="dropdownMenu" MenuStyle="BootStrapNav" CssClass="w-100" />""";
        const string template =
            """<ul class="navbar-nav d-flex justify-content-between [=CssClass] px-2">[*>NODE]<li>[=TEXT]</li>[/NODE]</ul>""";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("_default/Skins/FBOT/BootStrapNav/BootStrapNav.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(template);
        }
        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        string result = BundleWriter.ExpandMenuTemplateClasses(ascx, "_default/Skins/FBOT", archive);

        // Template literal classes should be prepended to existing CssClass.
        Assert.Contains(@"CssClass=""navbar-nav d-flex justify-content-between px-2 w-100""", result);
    }

    [Fact]
    public void ExpandMenuTemplateClasses_NoCssClass_InjectsTemplateClasses()
    {
        // MENU tag has MenuStyle but no CssClass at all.
        const string ascx =
            """<dnn:MENU runat="server" id="nav" MenuStyle="BootStrapNav" />""";
        const string template =
            """<ul class="navbar-nav d-flex [=CssClass]">[*>NODE][/NODE]</ul>""";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("Skins/Theme/BootStrapNav/BootStrapNav.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(template);
        }
        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        string result = BundleWriter.ExpandMenuTemplateClasses(ascx, "Skins/Theme", archive);

        Assert.Contains(@"CssClass=""navbar-nav d-flex""", result);
    }

    [Fact]
    public void ExpandMenuTemplateClasses_NoMenuStyle_PassesThrough()
    {
        // No MenuStyle attribute → tag should pass through unchanged.
        const string ascx =
            """<dnn:MENU runat="server" id="nav" CssClass="w-100" />""";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Empty archive — no template files.
        }
        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        string result = BundleWriter.ExpandMenuTemplateClasses(ascx, "Skins/Theme", archive);

        Assert.Equal(ascx, result);
    }

    [Fact]
    public void ExpandMenuTemplateClasses_TemplateMissing_PassesThrough()
    {
        // MenuStyle is set but no template file exists in the zip.
        const string ascx =
            """<dnn:MENU runat="server" MenuStyle="Missing" CssClass="w-100" />""";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Empty archive — no template files.
        }
        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        string result = BundleWriter.ExpandMenuTemplateClasses(ascx, "Skins/Theme", archive);

        Assert.Equal(ascx, result);
    }

    [Fact]
    public void ExpandMenuTemplateClasses_TemplateAllTokens_PassesThrough()
    {
        // Template root <ul> class is entirely tokens — no literal classes to add.
        const string ascx =
            """<dnn:MENU runat="server" MenuStyle="MyNav" CssClass="w-100" />""";
        const string template =
            """<ul class="[=CssClass]">[*>NODE][/NODE]</ul>""";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("Skins/T/MyNav/MyNav.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(template);
        }
        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        string result = BundleWriter.ExpandMenuTemplateClasses(ascx, "Skins/T", archive);

        // Only the token was in the class — no literal classes to add.
        Assert.Equal(ascx, result);
    }

    [Fact]
    public void ExpandMenuTemplateClasses_EndToEnd_NavSnippetHasFullClasses()
    {
        // Verify the full pipeline: ExpandMenuTemplateClasses + ConvertAscxToTemplateHtml.
        const string ascx =
            """<dnn:MENU runat="server" id="dropdownMenu" MenuStyle="BootStrapNav" CssClass="w-100" />""";
        const string template =
            """<ul class="navbar-nav d-flex justify-content-between [=CssClass] px-2">[*>NODE]<li>[=TEXT]</li>[/NODE]</ul>""";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("_default/Skins/FBOT/BootStrapNav/BootStrapNav.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(template);
        }
        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        // Step 1: expand template classes into the CssClass attribute.
        string expanded = BundleWriter.ExpandMenuTemplateClasses(ascx, "_default/Skins/FBOT", archive);

        // Step 2: convert to template HTML (BuildNavSnippet will read the expanded CssClass).
        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(expanded);

        // The generated <ul> should have all classes from both template and CssClass.
        Assert.Contains(@"class=""navbar-nav d-flex justify-content-between px-2 w-100""", body);
        Assert.Contains(@"id=""dropdownMenu""", body);
        Assert.Contains("$navtool.getNav", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_ReplacesBreadcrumbControl()
    {
        const string ascx = """
            <div><dnn:BREADCRUMB runat="server" /></div>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains("dnn-breadcrumb", body);
        Assert.Contains("$crumbTool.getCrumbs", body);
        Assert.Contains("#foreach", body);
        Assert.DoesNotContain("dnn:BREADCRUMB", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_RemovesStylesAndJquery()
    {
        const string ascx = """
            <dnn:STYLES runat="server" id="styles" />
            <dnn:jQuery runat="server" id="jquery" />
            <div id="body"></div>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.DoesNotContain("dnn:STYLES", body);
        Assert.DoesNotContain("dnn:jQuery", body);
        Assert.Contains("id=\"body\"", body);
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

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains("id=\"siteWrapper\"", body);
        Assert.Contains("id=\"header\"", body);
        Assert.Contains("id=\"footer\"", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_WithThemeName_PrependsSkinCssLink()
    {
        // When a theme name is supplied, a <link> tag for the theme's skin.css
        // must appear in the template body so DotCMS loads the skin styles.
        const string ascx = """<div id="body"></div>""";

        var (body, header, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx, themeName: "Xcillion");

        Assert.Contains(@"<link rel=""stylesheet"" href=""/application/themes/Xcillion/skin.css""", body);
        Assert.Empty(header);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_WithoutThemeName_DoesNotPrependCssLink()
    {
        // Without a theme name no CSS link should be injected.
        const string ascx = """<div id="body"></div>""";

        var (body, header, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.DoesNotContain("<link", body);
        Assert.DoesNotContain("<link", header);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_WithThemeName_LogoUsesThemePath()
    {
        // <dnn:LOGO> must be replaced with an <img> pointing to the theme's
        // Images/logo.png when a theme name is provided.
        const string ascx = """<div><dnn:LOGO runat="server" id="logo" /></div>""";

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx, themeName: "Xcillion");

        Assert.Contains("/application/themes/Xcillion/Images/logo.png", body);
        Assert.DoesNotContain("dnn:LOGO", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_WithoutThemeName_LogoUsesGenericPath()
    {
        // Without a theme name the <dnn:LOGO> fallback path /logo.png is used.
        const string ascx = """<div><dnn:LOGO runat="server" id="logo" /></div>""";

        var (body, header, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains("/logo.png", body);
        Assert.DoesNotContain("dnn:LOGO", body);
        Assert.DoesNotContain("/application/themes/", body);
        Assert.DoesNotContain("/application/themes/", header);
    }

    // ------------------------------------------------------------------
    // DnnCssInclude / DnnJsInclude → <link> / <script> conversion
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertAscxToTemplateHtml_ConvertsDnnCssIncludeToLinkTag()
    {
        const string ascx = """
            <dnn:DnnCssInclude ID="BootstrapCSS" runat="server"
                FilePath="bootstrap/css/bootstrap.min.css" PathNameAlias="SkinPath" Priority="12" />
            <div id="body"></div>
            """;

        var (body, header, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx, themeName: "Xcillion");

        // CSS link must appear in body.
        Assert.Contains(
            @"<link rel=""stylesheet"" href=""/application/themes/Xcillion/bootstrap/css/bootstrap.min.css"" />",
            body);
        Assert.DoesNotContain("DnnCssInclude", body);
        Assert.DoesNotContain("DnnCssInclude", header);
        Assert.Empty(header);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_ConvertsDnnJsIncludeToScriptTag()
    {
        const string ascx = """
            <dnn:DnnJsInclude ID="BootstrapJS" runat="server"
                FilePath="bootstrap/js/bootstrap.min.js" PathNameAlias="SkinPath" />
            <div id="body"></div>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx, themeName: "Xcillion");

        // JS stays in the body (not the header).
        Assert.Contains(
            @"<script src=""/application/themes/Xcillion/bootstrap/js/bootstrap.min.js""></script>",
            body);
        Assert.DoesNotContain("DnnJsInclude", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_SkipsSkinCssPrependWhenDnnCssIncludeProvidesSkinCss()
    {
        // When a DnnCssInclude already specifies skin.css, the automatic
        // skin.css <link> must be skipped to avoid duplicates.
        const string ascx = """
            <dnn:DnnCssInclude ID="SkinCSS" runat="server" FilePath="skin.css" PathNameAlias="SkinPath" />
            <div id="body"></div>
            """;

        var (body, header, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx, themeName: "Xcillion");

        // Exactly one skin.css reference should appear (in body).
        int count = 0;
        int idx = -1;
        while ((idx = body.IndexOf("skin.css", idx + 1, StringComparison.Ordinal)) >= 0)
            count++;
        Assert.Equal(1, count);
        Assert.Empty(header);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_WithoutThemeName_DnnCssIncludeStrippedByGenericCleanup()
    {
        // Without a theme name the DnnCssInclude tags cannot be rewritten,
        // so they fall through to the generic DNN-tag cleanup.
        const string ascx = """
            <dnn:DnnCssInclude ID="SkinCSS" runat="server" FilePath="skin.css" PathNameAlias="SkinPath" />
            <div id="body"></div>
            """;

        var (body, header, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.DoesNotContain("DnnCssInclude", body);
        Assert.DoesNotContain("<link", body);
        Assert.DoesNotContain("<link", header);
    }

    // ------------------------------------------------------------------
    // Per-skin CSS injection (e.g. Home.css for Home.ascx)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertAscxToTemplateHtml_WithSkinName_PrependsSkinSpecificCssLink()
    {
        // DNN auto-loads [SkinName].css from the skin folder alongside
        // the skin's ASCX file.  The converter must inject this link in the body.
        const string ascx = """<div id="body"></div>""";

        var (body, header, _) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, themeName: "Xcillion", skinName: "Home");

        Assert.Contains(
            @"<link rel=""stylesheet"" href=""/application/themes/Xcillion/Home.css""",
            body);
        Assert.Empty(header);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_SkinNameSkin_DoesNotDuplicateSkinCss()
    {
        // When the skin name is literally "skin", the per-skin link would
        // duplicate the skin.css link already injected – it must be skipped.
        const string ascx = """<div id="body"></div>""";

        var (body, header, _) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, themeName: "Xcillion", skinName: "skin");

        int count = 0;
        int idx = -1;
        while ((idx = body.IndexOf("skin.css", idx + 1, StringComparison.Ordinal)) >= 0)
            count++;
        Assert.Equal(1, count);
        Assert.Empty(header);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_WithoutSkinName_DoesNotInjectPerSkinCss()
    {
        // When no skin name is supplied, only the shared skin.css link
        // should appear in the body – no per-skin link should be added.
        const string ascx = """<div id="body"></div>""";

        var (body, header, _) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, themeName: "Xcillion");

        Assert.Contains("skin.css", body);
        Assert.DoesNotContain("Home.css", body);
        Assert.Empty(header);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_SkinCssPrecedesSkinSpecificCss()
    {
        // skin.css must appear before the per-skin CSS in the body to
        // mirror DNN's loading order where global skin styles load first.
        const string ascx = """<div id="body"></div>""";

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, themeName: "Xcillion", skinName: "Home");

        int skinCssIndex = body.IndexOf("skin.css", StringComparison.Ordinal);
        int homeCssIndex = body.IndexOf("Home.css", StringComparison.Ordinal);
        Assert.True(skinCssIndex >= 0, "skin.css link must be present.");
        Assert.True(homeCssIndex >= 0, "Home.css link must be present.");
        Assert.True(skinCssIndex < homeCssIndex,
            "skin.css link must precede per-skin CSS link (DNN loads base skin styles first).");
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_SkipsDuplicatePerSkinCssWhenAlreadyPresent()
    {
        // If the per-skin CSS is already referenced (e.g. via a
        // DnnCssInclude), it should not be injected a second time.
        const string ascx = """
            <dnn:DnnCssInclude ID="HomeCSS" runat="server" FilePath="Home.css" PathNameAlias="SkinPath" />
            <div id="body"></div>
            """;

        var (body, header, _) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, themeName: "Xcillion", skinName: "Home");

        int count = 0;
        int idx = -1;
        while ((idx = body.IndexOf("Home.css", idx + 1, StringComparison.Ordinal)) >= 0)
            count++;
        Assert.Equal(1, count);
        Assert.Empty(header);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_AvailableThemeFiles_SkipsMissingSkinCss()
    {
        // When availableThemeFiles is provided but skin.css is absent,
        // no skin.css link should be injected.
        const string ascx = """<div id="body"></div>""";
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, themeName: "Xcillion", availableThemeFiles: available);

        Assert.DoesNotContain("skin.css", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_AvailableThemeFiles_AlwaysInjectsPerSkinCss()
    {
        // Per-skin CSS links are always injected regardless of
        // availableThemeFiles because WriteThemeFileAssets creates a
        // placeholder CSS file when the export does not include one.
        const string ascx = """<div id="body"></div>""";
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "application/themes/Xcillion/skin.css"
        };

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, themeName: "Xcillion", skinName: "Home",
            availableThemeFiles: available);

        Assert.Contains("skin.css", body);
        Assert.Contains("Home.css", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_AvailableThemeFiles_InjectsPresentCss()
    {
        // When both skin.css and Home.css exist in the set, both links
        // should be injected.
        const string ascx = """<div id="body"></div>""";
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "application/themes/Xcillion/skin.css",
            "application/themes/Xcillion/Home.css"
        };

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, themeName: "Xcillion", skinName: "Home",
            availableThemeFiles: available);

        Assert.Contains("skin.css", body);
        Assert.Contains("Home.css", body);
    }

    // ------------------------------------------------------------------
    // SSI include resolution
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveSsiIncludes_InlinesReferencedFileFromZip()
    {
        string path = Path.GetTempFileName() + ".zip";
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                ZipArchiveEntry main = zip.CreateEntry("_default/Skins/TestTheme/Home.ascx");
                using (var w = new StreamWriter(main.Open()))
                    w.Write("""<div>before</div><!--#include file="Common/AddFiles.ascx"--><div>after</div>""");

                ZipArchiveEntry inc = zip.CreateEntry("_default/Skins/TestTheme/Common/AddFiles.ascx");
                using (var w = new StreamWriter(inc.Open()))
                    w.Write("<link>INLINED</link>");
            }

            using var archive = ZipFile.OpenRead(path);
            ZipArchiveEntry homeEntry = archive.GetEntry("_default/Skins/TestTheme/Home.ascx")!;
            using var reader = new StreamReader(homeEntry.Open(), Encoding.UTF8);
            string ascx = reader.ReadToEnd();

            string result = BundleWriter.ResolveSsiIncludes(
                ascx, "_default/Skins/TestTheme", archive);

            Assert.Contains("<div>before</div>", result);
            Assert.Contains("<link>INLINED</link>", result);
            Assert.Contains("<div>after</div>", result);
            Assert.DoesNotContain("#include", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveSsiIncludes_RemovesDirectiveWhenFileNotFound()
    {
        string path = Path.GetTempFileName() + ".zip";
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                ZipArchiveEntry main = zip.CreateEntry("_default/Skins/TestTheme/Home.ascx");
                using (var w = new StreamWriter(main.Open()))
                    w.Write("""<div>main</div><!--#include file="Missing.ascx"-->""");
            }

            using var archive = ZipFile.OpenRead(path);
            ZipArchiveEntry homeEntry = archive.GetEntry("_default/Skins/TestTheme/Home.ascx")!;
            using var reader = new StreamReader(homeEntry.Open(), Encoding.UTF8);
            string ascx = reader.ReadToEnd();

            string result = BundleWriter.ResolveSsiIncludes(
                ascx, "_default/Skins/TestTheme", archive);

            Assert.Contains("<div>main</div>", result);
            Assert.DoesNotContain("#include", result);
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------
    // SkinPath expression replacement
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertAscxToTemplateHtml_ReplacesSkinPathInScriptTags()
    {
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div id="ContentPane" runat="server"></div>
            <script src="<%= SkinPath %>js/custom.js"></script>
            <script src="<%= SkinPath %>/modal/modal.js"></script>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, "ctr1", themeName: "fbot");

        Assert.Contains(@"src=""/application/themes/fbot/js/custom.js""", body);
        Assert.Contains(@"src=""/application/themes/fbot//modal/modal.js""", body);
        Assert.DoesNotContain("SkinPath", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_ReplacesSkinPathInLinkTags()
    {
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <link src="<%= SkinPath %>/modal/modal.css">
            <div id="ContentPane" runat="server"></div>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, "ctr1", themeName: "fbot");

        Assert.Contains(@"src=""/application/themes/fbot//modal/modal.css""", body);
        Assert.DoesNotContain("SkinPath", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_WithoutThemeName_SkinPathExpressionStripped()
    {
        // Without a theme name, SkinPath cannot be resolved; the generic
        // CodeBlockRegex cleanup removes the expression.
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <script src="<%= SkinPath %>js/custom.js"></script>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.DoesNotContain("SkinPath", body);
        // Path is left relative (no theme base) after code-block stripping.
        Assert.Contains(@"src=""js/custom.js""", body);
    }

    // ------------------------------------------------------------------
    // Registered control resolution
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveRegisteredControls_InlinesLocalUserControl()
    {
        string path = Path.GetTempFileName() + ".zip";
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                ZipArchiveEntry main = zip.CreateEntry("Skins/Test/Home.ascx");
                using (var w = new StreamWriter(main.Open()))
                    w.Write("""
                        <%@ Register TagPrefix="FIS" TagName="Modal" Src="modal/modal.ascx" %>
                        <div>main</div>
                        <FIS:Modal id="Modal" runat="server" />
                        """);

                ZipArchiveEntry modal = zip.CreateEntry("Skins/Test/modal/modal.ascx");
                using (var w = new StreamWriter(modal.Open()))
                    w.Write("""
                        <%@ Control Inherits="DotNetNuke.UI.Skins.SkinObjectBase" %>
                        <div class="modal">Modal Content</div>
                        """);
            }

            using var archive = ZipFile.OpenRead(path);
            ZipArchiveEntry homeEntry = archive.GetEntry("Skins/Test/Home.ascx")!;
            using var reader = new StreamReader(homeEntry.Open(), Encoding.UTF8);
            string ascx = reader.ReadToEnd();

            string result = BundleWriter.ResolveRegisteredControls(
                ascx, "Skins/Test", archive);

            Assert.Contains("<div class=\"modal\">Modal Content</div>", result);
            Assert.DoesNotContain("<FIS:Modal", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveRegisteredControls_IgnoresSystemControls()
    {
        // Controls with Src starting with ~ (DNN system controls) should
        // not be resolved — they are handled by other processing steps.
        string path = Path.GetTempFileName() + ".zip";
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                ZipArchiveEntry main = zip.CreateEntry("Skins/Test/Home.ascx");
                using (var w = new StreamWriter(main.Open()))
                    w.Write("""
                        <%@ Register TagPrefix="dnn" TagName="LOGO" Src="~/Admin/Skins/Logo.ascx" %>
                        <dnn:LOGO runat="server" />
                        """);
            }

            using var archive = ZipFile.OpenRead(path);
            ZipArchiveEntry homeEntry = archive.GetEntry("Skins/Test/Home.ascx")!;
            using var reader = new StreamReader(homeEntry.Open(), Encoding.UTF8);
            string ascx = reader.ReadToEnd();

            string result = BundleWriter.ResolveRegisteredControls(
                ascx, "Skins/Test", archive);

            // System control tag should remain untouched.
            Assert.Contains("<dnn:LOGO", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveRegisteredControls_RemovesTagWhenSourceNotFound()
    {
        string path = Path.GetTempFileName() + ".zip";
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                ZipArchiveEntry main = zip.CreateEntry("Skins/Test/Home.ascx");
                using (var w = new StreamWriter(main.Open()))
                    w.Write("""
                        <%@ Register TagPrefix="FIS" TagName="Missing" Src="missing/control.ascx" %>
                        <div>main</div>
                        <FIS:Missing id="M" runat="server" />
                        """);
            }

            using var archive = ZipFile.OpenRead(path);
            ZipArchiveEntry homeEntry = archive.GetEntry("Skins/Test/Home.ascx")!;
            using var reader = new StreamReader(homeEntry.Open(), Encoding.UTF8);
            string ascx = reader.ReadToEnd();

            string result = BundleWriter.ResolveRegisteredControls(
                ascx, "Skins/Test", archive);

            Assert.DoesNotContain("<FIS:Missing", result);
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------
    // Pane div layout attribute preservation
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertAscxToTemplateHtml_PreservesBootstrapClassesOnPaneDivs()
    {
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div class="promo">
              <div class="row">
                <div class="col-md-6 bg-dark" id="PromoLeft" runat="server"></div>
                <div class="col-md-6" id="PromoRight" runat="server"></div>
              </div>
            </div>
            """;

        var (body, _, paneUuidMap) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, "ctr1", themeName: "TestTheme");

        // The wrapper divs with bootstrap classes should be preserved.
        Assert.Contains("class=\"col-md-6 bg-dark\"", body);
        Assert.Contains("class=\"col-md-6\"", body);
        // #parseContainer should be inside the wrapper divs.
        Assert.Contains("#parseContainer('ctr1',", body);
        // Pane IDs should still be mapped.
        Assert.True(paneUuidMap.ContainsKey("PromoLeft"));
        Assert.True(paneUuidMap.ContainsKey("PromoRight"));
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_PlainPaneDivFullyReplaced()
    {
        // Pane divs without extra classes should be replaced entirely
        // (no wrapper div) to keep existing behaviour.
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div id="ContentPane" runat="server"></div>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, "ctr1");

        Assert.Contains("#parseContainer('ctr1',", body);
        // There should be no leftover <div> wrapper around the #parseContainer.
        Assert.DoesNotContain("<div", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_MixedPaneDivsHandledCorrectly()
    {
        // Mix of pane divs with and without classes.
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div id="ContentPane" runat="server"></div>
            <div class="col-md-3" id="FooterLeft" runat="server"></div>
            """;

        var (body, _, paneUuidMap) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, "ctr1");

        // ContentPane (no extra class) — no wrapper
        Assert.Contains("#parseContainer('ctr1', '1')", body);
        // FooterLeft (has class) — wrapper preserved
        Assert.Contains("class=\"col-md-3\"", body);
        Assert.True(paneUuidMap.ContainsKey("ContentPane"));
        Assert.True(paneUuidMap.ContainsKey("FooterLeft"));
    }

    [Fact]
    public void Write_WithSsiIncludeInSkin_InlinedCssIncludesAppearInTemplateXml()
    {
        // DnnCssInclude tags should produce a template with proper <link> tags
        // in the header field and <script> tags in the body.
        string path = Path.GetTempFileName() + ".zip";
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                ZipArchiveEntry container =
                    zip.CreateEntry("_default/Containers/TestTheme/Boxed.ascx");
                using (var w = new StreamWriter(container.Open()))
                    w.Write("""<div id="ContentPane" runat="server"></div>""");

                ZipArchiveEntry skin =
                    zip.CreateEntry("_default/Skins/TestTheme/Home.ascx");
                using (var w = new StreamWriter(skin.Open()))
                    w.Write("""
                        <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
                        <div id="ContentPane" runat="server"></div>
                        <!--#include file="Common/AddFiles.ascx"-->
                        """);

                ZipArchiveEntry addFiles =
                    zip.CreateEntry("_default/Skins/TestTheme/Common/AddFiles.ascx");
                using (var w = new StreamWriter(addFiles.Open()))
                    w.Write("""
                        <dnn:DnnCssInclude ID="BootstrapCSS" runat="server"
                            FilePath="bootstrap/css/bootstrap.min.css" PathNameAlias="SkinPath" Priority="12" />
                        <dnn:DnnCssInclude ID="SkinCSS" runat="server"
                            FilePath="skin.css" PathNameAlias="SkinPath" />
                        <dnn:DnnJsInclude ID="ScriptJS" runat="server"
                            FilePath="js/scripts.js" PathNameAlias="SkinPath" />
                        """);
            }

            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], path);

            // Read the template XML to verify it contains the converted CSS/JS links.
            string templateXml = names
                .Where(n => n.EndsWith(".template.template.xml"))
                .Select(n => ReadTarEntry(ms, n)!)
                .First();

            Assert.Contains("/application/themes/TestTheme/bootstrap/css/bootstrap.min.css", templateXml);
            Assert.Contains("/application/themes/TestTheme/skin.css", templateXml);
            Assert.Contains("/application/themes/TestTheme/js/scripts.js", templateXml);
            Assert.DoesNotContain("DnnCssInclude", templateXml);
            Assert.DoesNotContain("DnnJsInclude", templateXml);
            Assert.DoesNotContain("#include", templateXml);
        }
        finally { File.Delete(path); }
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
    // Static theme asset path tests
    // ------------------------------------------------------------------

    /// <summary>Creates a themes ZIP that contains one static CSS file under each prefix.</summary>
    private static string BuildThemesZipWithStaticAssets()
    {
        string path = Path.GetTempFileName() + ".zip";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        // CSS under a skin
        ZipArchiveEntry skinCss = zip.CreateEntry("_default/Skins/MyTheme/skin.css");
        using (var w = new StreamWriter(skinCss.Open()))
            w.Write("body{}");

        // Image under a container
        ZipArchiveEntry containerImg = zip.CreateEntry("_default/Containers/MyTheme/title.png");
        using (var w2 = new BinaryWriter(containerImg.Open()))
            w2.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        return path;
    }

    [Fact]
    public void Write_WithThemesZip_StaticSkinAssetsGoToApplicationThemesFolder()
    {
        // Static files from "_default/Skins/{ThemeName}/…" must be stored as
        // proper FileAsset contentlets: binary under assets/ and content XML
        // referencing the /application/themes/ folder path.
        string path = BuildThemesZipWithStaticAssets();
        try
        {
            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], path);

            // CSS file bytes should be stored under assets/{x}/{y}/{inode}/fileAsset/skin.css
            Assert.Contains(names, n => n.StartsWith("assets/") && n.EndsWith("/skin.css"));
            // A content XML entry should exist with /application/themes/ in its parentPath.
            Assert.Contains(names, n => n.StartsWith("live/") && n.EndsWith(".content.xml"));
            Assert.DoesNotContain(names, n => n.StartsWith("ROOT/") && n.EndsWith(".css"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_WithThemesZip_StaticContainerAssetsGoToApplicationThemesFolder()
    {
        // Static files from "_default/Containers/{ThemeName}/…" must also be
        // stored as FileAsset contentlets, not raw files under ROOT/.
        string path = BuildThemesZipWithStaticAssets();
        try
        {
            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], path);

            // PNG file bytes should be stored under assets/{x}/{y}/{inode}/fileAsset/title.png
            Assert.Contains(names, n => n.StartsWith("assets/") && n.EndsWith("/title.png"));
            Assert.DoesNotContain(names, n => n.StartsWith("ROOT/") && n.EndsWith(".png"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_WithThemesZip_NoStaticFilesPlacedAtBundleRoot()
    {
        // No static asset file should be at the tar root level (no directory prefix).
        // (manifest.csv is intentionally at the root — that is expected bundle behaviour.)
        string path = BuildThemesZipWithStaticAssets();
        try
        {
            var (_, names) = WriteBundleToMemory([MakeHtmlContentType()], path);

            // All entries except "manifest.csv" must have at least one "/" in their path.
            Assert.All(names.Where(n => n != "manifest.csv"),
                n => Assert.Contains("/", n));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_WithThemesZip_CreatesPlaceholderPerSkinCssFile()
    {
        // The themes zip has Home.ascx but no Home.css.
        // WriteThemeFileAssets must create a placeholder Home.css so
        // the per-skin CSS link in the template resolves to a real file.
        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);

            // A binary asset entry for Home.css must exist.
            Assert.Contains(names, n => n.StartsWith("assets/") && n.EndsWith("/Home.css"));
        }
        finally { File.Delete(zipPath); }
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
        while ((entry = tar.GetNextEntry(copyData: true)) is not null)
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

        // "My Website" should be sanitized to "My-Website"
        Assert.Contains("My-Website", xml);
        Assert.Contains("<string>hostname</string>", xml);
    }

    [Fact]
    public void Write_WithSiteName_ManifestIncludesHostRow()
    {
        var (ms, _) = WriteBundleWithSite("My Website");
        string manifest = ReadTarEntry(ms, "manifest.csv")!;

        Assert.Contains("INCLUDED,host,", manifest);
        Assert.Contains("My-Website", manifest);
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
                n.StartsWith("live/My-Website/") &&
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
                n.StartsWith("live/My-Website/") &&
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
            n.StartsWith("working/My-Website/") &&
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

        Assert.Equal("My-Website", siteName);
    }

    [Fact]
    public void Write_WithSiteName_ManifestContentTypeRowUsesSiteName()
    {
        var (ms, _) = WriteBundleWithSite("My Website");
        string manifest = ReadTarEntry(ms, "manifest.csv")!;

        string ctLine = manifest
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(l => l.StartsWith("INCLUDED,contenttype,"));

        // The site column should be "My-Website", not "System Host".
        Assert.Contains("My-Website", ctLine);
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
    [InlineData("My Website",        "My-Website")]
    [InlineData("DNN Site Export",   "DNN-Site-Export")]
    [InlineData("Hello World!",      "Hello-World")]
    [InlineData("  spaces  ",        "spaces")]
    [InlineData("A",                 "A")]
    [InlineData("",                  "imported-site")]
    [InlineData("---",               "imported-site")]
    public void SanitizeHostname_ProducesExpectedResult(string input, string expected)
    {
        Assert.Equal(expected, BundleWriter.SanitizeHostname(input));
    }

    // ------------------------------------------------------------------
    // DeterministicId utility tests
    // ------------------------------------------------------------------

    [Fact]
    public void DeterministicId_SameSeed_ReturnsSameId()
    {
        string id1 = BundleWriter.DeterministicId("ContentType:htmlContent");
        string id2 = BundleWriter.DeterministicId("ContentType:htmlContent");
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void DeterministicId_DifferentSeed_ReturnsDifferentId()
    {
        string id1 = BundleWriter.DeterministicId("ContentType:htmlContent");
        string id2 = BundleWriter.DeterministicId("ContentType:otherType");
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void DeterministicId_IsValidGuid()
    {
        string id = BundleWriter.DeterministicId("test-seed");
        Assert.True(Guid.TryParse(id, out _), "DeterministicId must return a valid GUID string.");
    }

    [Fact]
    public void Write_SameContentType_ProducesSameIdAcrossBundles()
    {
        // Two independent bundles with the same content type variable
        // must produce the same content type ID so DotCMS treats the
        // second import as an update rather than a conflicting insert.
        var contentType = new[] { MakeHtmlContentType() };

        var (ms1, names1) = WriteBundleToMemory(contentType);
        var (ms2, names2) = WriteBundleToMemory(contentType);

        string ctEntry1 = names1.First(n => n.EndsWith(".contentType.json"));
        string ctEntry2 = names2.First(n => n.EndsWith(".contentType.json"));

        // The content type JSON filename includes the content type ID.
        // Both bundles must use the same filename (same ID).
        string ctId1 = Path.GetFileName(ctEntry1);
        string ctId2 = Path.GetFileName(ctEntry2);
        Assert.Equal(ctId1, ctId2);
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
        while ((entry = tar.GetNextEntry(copyData: true)) is not null)
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
        while ((entry = tar.GetNextEntry(copyData: true)) is not null)
            names.Add(entry.Name);

        Assert.DoesNotContain(names, n => n.EndsWith(".content.xml") && n.Contains("/1/"));
    }

    [Fact]
    public void Write_WithSiteName_ContentletsGoToSiteDirectory()
    {
        var (_, names) = WriteBundleWithContents(MakeHtmlContents(), siteName: "My Website");

        Assert.Contains(names, n =>
            n.StartsWith("live/My-Website/1/") &&
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
        while ((entry = tar.GetNextEntry(copyData: true)) is not null)
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

        // Page title in DotCMS uses the DNN Name (TabName), not Title.
        Assert.Contains("Home", xml);
    }

    [Fact]
    public void Write_WithPages_PageTitleUsesNameNotTitle()
    {
        var pages = new[]
        {
            new DnnPortalPage("bbb", "About Us", "About Us – Our Company", "", "//About Us", 0, true, ""),
        };

        var (ms, names) = WriteBundleWithPages(pages);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        // DotCMS page title should be the DNN Name (with spaces), not the Title.
        Assert.Contains("About Us", xml);
        Assert.DoesNotContain("Our Company", xml);
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
    public void Write_WithPages_SubPagesAreIncludedWithFolder()
    {
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Home",       "Home",       "", "//Home",           0, true, ""),
            new DnnPortalPage("bbb", "My Profile", "My Profile", "", "//Activity//MyProfile", 1, true, ""),
        };

        var (_, names) = WriteBundleWithPages(pages);

        // Both the Level-0 Home page and the Level-1 My Profile page should
        // produce content.xml entries (child pages are now included).
        int pageCount = names.Count(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml") && !n.Contains("htmlContent"));
        Assert.Equal(2, pageCount);
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
    // Fallback container / template tests (no themes zip)
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithPagesAndContentsButNoThemes_CreatesDefaultContainer()
    {
        // When there is no themes zip but pages and content exist, a default
        // "Standard" container should be created automatically so that
        // multiTree linkage works.
        string tabId = "tab-fallback-1";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "Home", "Home", "", "//Home", 0, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Welcome", "<h1>Hello</h1>",
                TabUniqueId: tabId, PaneName: "ContentPane"),
        };

        var (ms, names) = WriteBundleWithPagesAndContents(pages, contents);

        // Should have a container XML entry.
        Assert.Contains(names, n => n.EndsWith(".containers.container.xml"));
    }

    [Fact]
    public void Write_WithPagesAndContentsButNoThemes_CreatesDefaultTemplate()
    {
        string tabId = "tab-fallback-2";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "About", "About", "", "//About", 0, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("About Us", "<p>About</p>",
                TabUniqueId: tabId, PaneName: "ContentPane"),
        };

        var (ms, names) = WriteBundleWithPagesAndContents(pages, contents);

        // Should have a template XML entry.
        Assert.Contains(names, n => n.EndsWith(".template.template.xml"));
    }

    [Fact]
    public void Write_WithPagesAndContentsButNoThemes_PageHasPopulatedMultiTree()
    {
        // The page's multiTree should link to the content item when fallback
        // container/template are used (the crawl-mode scenario).
        string tabId = "tab-fallback-3";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "Home", "Home", "", "//Home", 0, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Welcome", "<h1>Hello</h1>",
                TabUniqueId: tabId, PaneName: "ContentPane"),
        };

        var (ms, names) = WriteBundleWithPagesAndContents(pages, contents);

        // Find the page XML (htmlpageasset).
        string? pageXml = null;
        foreach (string name in names.Where(n =>
            n.Contains("/1/") && n.EndsWith(".content.xml") && !n.Contains("host.xml")))
        {
            string? xml = ReadTarEntry(ms, name);
            if (xml is not null && xml.Contains("htmlpageasset"))
            {
                pageXml = xml;
                break;
            }
        }

        Assert.NotNull(pageXml);
        Assert.Contains("<multiTree>", pageXml);
        Assert.DoesNotContain("<multiTree/>", pageXml);
    }

    [Fact]
    public void Write_WithPagesOnly_NoFallbackContainerOrTemplate()
    {
        // When there are pages but NO content, no fallback container/template
        // should be created (the fallback is only needed when linking content).
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Home", "Home", "", "//Home", 0, true, ""),
        };

        var (_, names) = WriteBundleWithPages(pages);

        Assert.DoesNotContain(names, n => n.EndsWith(".containers.container.xml"));
        Assert.DoesNotContain(names, n => n.EndsWith(".template.template.xml"));
    }

    [Fact]
    public void Write_CrawlModeFiles_PlacedUnderApplicationFolder()
    {
        // Crawl-mode portal files have FolderPath prefixed with "application/"
        // so that BundleWriter places them under ROOT/application/.
        var files = new[]
        {
            new DnnPortalFile(
                Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                "logo.png", "application/images/", "image/png", [1, 2, 3]),
            new DnnPortalFile(
                Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                "style.css", "application/css/", "text/css",
                Encoding.UTF8.GetBytes("body{}")),
            new DnnPortalFile(
                Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                "favicon.ico", "application/", "image/x-icon", [0]),
        };

        var (_, names) = WriteBundleWithFiles(files);

        // Files must be under ROOT/application/.
        Assert.Contains("ROOT/application/images/logo.png", names);
        Assert.Contains("ROOT/application/css/style.css", names);
        Assert.Contains("ROOT/application/favicon.ico", names);

        // Must NOT be at the site root.
        Assert.DoesNotContain("ROOT/images/logo.png", names);
        Assert.DoesNotContain("ROOT/css/style.css", names);
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
        while ((entry = tar.GetNextEntry(copyData: true)) is not null)
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
    public void Write_WithPortalFiles_RootFileAssetContentXmlFolderIsSystemFolder()
    {
        // A file at the DNN root (FolderPath = "") must use SYSTEM_FOLDER so that
        // DotCMS places it at the site root in the content tree.
        var files = new[]
        {
            new DnnPortalFile(
                "e5dfe1f2-4cdc-46bd-ad32-7257a6b8105a",
                "2af85195-c192-4a33-a14d-a8bb2dc6007e",
                "home.css", "", "text/css",
                Encoding.UTF8.GetBytes("/* css */")),
        };

        var (ms, names) = WriteBundleWithFiles(files, "Test Site");

        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml"));
        string xml = ReadTarEntry(ms, entryName)!;
        string folderValue = ExtractXmlStringField(xml, "folder");

        Assert.Equal("SYSTEM_FOLDER", folderValue);
    }

    [Fact]
    public void Write_WithPortalFiles_SubFolderFileAssetContentXmlFolderIsUuid()
    {
        // A file in a DNN sub-folder (FolderPath = "Images/") must reference a
        // generated folder UUID so DotCMS places it in the correct sub-folder.
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

        // Sub-folder files must use a generated UUID, not SYSTEM_FOLDER.
        Assert.NotEqual("SYSTEM_FOLDER", folderValue);
        Assert.True(Guid.TryParse(folderValue, out _),
            $"folder value should be a UUID, got: {folderValue}");
    }

    [Fact]
    public void Write_WithPortalFiles_SubFolderWritesFolderXmlEntry()
    {
        // When a portal file lives in a sub-folder, a FolderWrapper XML entry
        // must be included in the bundle so DotCMS creates the folder before
        // importing the file asset.
        var files = new[]
        {
            new DnnPortalFile(
                "6f574d5f-0880-4d5a-b4a2-74d2e10b5659",
                "69f363b0-6512-48ad-b187-b6a450ffda7b",
                "logo.png", "Images/", "image/png",
                [0x00]),
        };

        var (ms, names) = WriteBundleWithFiles(files, "Test Site");

        // A .folder.xml entry must appear under ROOT/.
        Assert.Contains(names, n => n.StartsWith("ROOT/") && n.EndsWith(".folder.xml"));
    }

    [Fact]
    public void Write_WithPortalFiles_FolderXmlContainsCorrectPath()
    {
        // The generated FolderWrapper XML must reference the DNN folder path so
        // DotCMS creates the folder at the right location in the content tree.
        var files = new[]
        {
            new DnnPortalFile(
                "6f574d5f-0880-4d5a-b4a2-74d2e10b5659",
                "69f363b0-6512-48ad-b187-b6a450ffda7b",
                "logo.png", "Images/", "image/png",
                [0x00]),
        };

        var (ms, names) = WriteBundleWithFiles(files, "Test Site");

        string folderEntry = names.First(n => n.StartsWith("ROOT/") && n.EndsWith(".folder.xml"));
        string xml = ReadTarEntry(ms, folderEntry)!;

        Assert.Contains("<name>Images</name>", xml);
        Assert.Contains("<path>/Images/</path>", xml);
        Assert.Contains("<parentPath>/</parentPath>", xml);
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

    [Fact]
    public void Write_WithPortalFiles_ImagesFolderFilesWrittenToRootImages()
    {
        // Files from the DNN "Images/" folder must appear as static files under
        // ROOT/Images/ in the bundle so that DotCMS serves them at /Images/
        // — the path referenced in converted HTML content.
        var files = new[]
        {
            new DnnPortalFile(
                "6f574d5f-0880-4d5a-b4a2-74d2e10b5659",
                "69f363b0-6512-48ad-b187-b6a450ffda7b",
                "logo.png", "Images/", "image/png",
                [0x89, 0x50, 0x4E, 0x47]),
        };

        var (_, names) = WriteBundleWithFiles(files);

        Assert.Contains("ROOT/Images/logo.png", names);
    }

    [Fact]
    public void Write_WithPortalFiles_RootFilesNotWrittenToImagesFolder()
    {
        // Files at the DNN site root (FolderPath = "") must NOT appear under
        // ROOT/Images/ — only Images/ sub-folder files go there.
        var files = new[]
        {
            new DnnPortalFile(
                "e5dfe1f2-4cdc-46bd-ad32-7257a6b8105a",
                "2af85195-c192-4a33-a14d-a8bb2dc6007e",
                "home.css", "", "text/css",
                Encoding.UTF8.GetBytes("/* css */")),
        };

        var (_, names) = WriteBundleWithFiles(files);

        Assert.DoesNotContain(names, n => n.StartsWith("ROOT/Images/"));
    }

    [Fact]
    public void Write_WithPortalFiles_ImagesFolderSubdirFilePreservesSubdirInRootImages()
    {
        // A file in a DNN sub-folder of Images/ (e.g. Images/Banners/) should land
        // at ROOT/Images/Banners/{filename} preserving the sub-structure.
        var files = new[]
        {
            new DnnPortalFile(
                "aaaaaaaa-0000-0000-0000-000000000001",
                "bbbbbbbb-0000-0000-0000-000000000001",
                "banner.jpg", "Images/Banners/", "image/jpeg",
                [0xFF, 0xD8, 0xFF]),
        };

        var (_, names) = WriteBundleWithFiles(files);

        Assert.Contains("ROOT/Images/Banners/banner.jpg", names);
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
        while ((entry = tar.GetNextEntry(copyData: true)) is not null)
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

    [Fact]
    public void Write_WithPageAndMatchingContent_MultiTreeRelationTypeIsSequentialInteger()
    {
        // The relation_type in multiTree entries must match the second argument of
        // the #parseContainer directives in the template body.  When content items
        // carry a PaneName that matches a pane in the template, the relation_type
        // is set to the pane's UUID slot.  When PaneName is empty, items placed
        // on the same page share the first available slot.
        string tabId = "seq-rel-type-test";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "Home", "Home", "", "//Home", 0, true, ""),
        };
        // Use a template with two panes so each content item maps to a distinct slot.
        string skinAscx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div id="ContentPane" runat="server"></div>
            <div id="FooterPane" runat="server"></div>
            """;
        var contents = new[]
        {
            new DnnHtmlContent("First",  "<p>first</p>",  TabUniqueId: tabId, PaneName: "ContentPane"),
            new DnnHtmlContent("Second", "<p>second</p>", TabUniqueId: tabId, PaneName: "FooterPane"),
        };

        string zipPath = BuildThemesZip(skinAscx: skinAscx);
        try
        {
            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents,
                themesZipPath: zipPath);

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
            // Both relation_type values must appear: "1" for ContentPane, "2" for FooterPane.
            Assert.Contains("<string>relation_type</string>", pageXml!);
            int idx1 = pageXml!.IndexOf("<string>relation_type</string>", StringComparison.Ordinal);
            Assert.True(idx1 >= 0);
            string afterFirst = pageXml[(idx1 + "<string>relation_type</string>".Length)..];
            Assert.Contains("<string>1</string>", afterFirst);
            int idx2 = pageXml.IndexOf("<string>relation_type</string>", idx1 + 1, StringComparison.Ordinal);
            Assert.True(idx2 >= 0);
            string afterSecond = pageXml[(idx2 + "<string>relation_type</string>".Length)..];
            Assert.Contains("<string>2</string>", afterSecond);
        }
        finally { File.Delete(zipPath); }
    }

    // ------------------------------------------------------------------
    // Template theme tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithTheme_TemplateXmlContainsThemePath()
    {
        // When templates are collected from a themes zip, each template's XML
        // must include a <theme> element pointing to the DotCMS application theme
        // path derived from the skin directory name.
        string zipPath = BuildThemesZip();
        try
        {
            var ms = new MemoryStream();
            BundleWriter.Write([MakeHtmlContentType()], ms, zipPath, "Test Site");
            ms.Position = 0;

            var names = new List<string>();
            using (var gz  = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true))
            using (var tar = new TarReader(gz))
            {
                TarEntry? entry;
                while ((entry = tar.GetNextEntry(copyData: true)) is not null)
                    names.Add(entry.Name);
            }
            ms.Position = 0;

            string templateEntry = names.First(n => n.EndsWith(".template.template.xml"));
            string xml = ReadTarEntry(ms, templateEntry)!;

            // The theme path must reference /application/themes/{themeName}
            Assert.Contains("<theme>/application/themes/TestTheme</theme>", xml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithoutTheme_TemplateXmlHasEmptyTheme()
    {
        // Without a themes zip, any template written must have an empty <theme>.
        var ms = new MemoryStream();
        BundleWriter.Write([MakeHtmlContentType()], ms, themesZipPath: null, siteName: "Test Site");
        ms.Position = 0;

        var names = new List<string>();
        using (var gz  = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true))
        using (var tar = new TarReader(gz))
        {
            TarEntry? entry;
            while ((entry = tar.GetNextEntry(copyData: true)) is not null)
                names.Add(entry.Name);
        }

        // No template entries should be present (no themes zip → no templates).
        Assert.DoesNotContain(names, n => n.EndsWith(".template.template.xml"));
    }

    [Fact]
    public void Write_WithTheme_TemplateHeaderContainsSkinCssLink()
    {
        // The converted template must include a <link> tag for skin.css in
        // the <body> XML field so DotCMS loads the theme styles.
        string zipPath = BuildThemesZip();
        try
        {
            var ms = new MemoryStream();
            BundleWriter.Write([MakeHtmlContentType()], ms, zipPath, "Test Site");
            ms.Position = 0;

            var names = new List<string>();
            using (var gz  = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true))
            using (var tar = new TarReader(gz))
            {
                TarEntry? entry;
                while ((entry = tar.GetNextEntry(copyData: true)) is not null)
                    names.Add(entry.Name);
            }
            ms.Position = 0;

            string templateEntry = names.First(n => n.EndsWith(".template.template.xml"));
            string xml = ReadTarEntry(ms, templateEntry)!;

            // The template body must contain a skin.css link for the theme.
            Assert.Contains("skin.css", xml);
            Assert.Contains("/application/themes/TestTheme/skin.css", xml);
            // Verify the CSS link is in the <body> element.
            int bodyStart = xml.IndexOf("<body>", StringComparison.Ordinal);
            int bodyEnd   = xml.IndexOf("</body>", StringComparison.Ordinal);
            Assert.True(bodyStart >= 0 && bodyEnd > bodyStart,
                "Template XML must contain a <body> element.");
            string bodyContent = xml[bodyStart..bodyEnd];
            Assert.Contains("skin.css", bodyContent);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithTheme_TemplateBodyLogoUsesThemePath()
    {
        // When a <dnn:LOGO> control appears in the skin, it must be replaced
        // with an <img> pointing to the theme's Images/logo.png path.
        string zipPath = Path.GetTempFileName() + ".zip";
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry skin = zip.CreateEntry("_default/Skins/TestTheme/Home.ascx");
                using (var w = new StreamWriter(skin.Open()))
                    w.Write("""<div><dnn:LOGO runat="server" id="logo" /></div>""");

                ZipArchiveEntry container = zip.CreateEntry("_default/Containers/TestTheme/C.ascx");
                using (var w = new StreamWriter(container.Open()))
                    w.Write("""<div id="ContentPane" runat="server"></div>""");
            }

            var ms = new MemoryStream();
            BundleWriter.Write([MakeHtmlContentType()], ms, zipPath, "Test Site");
            ms.Position = 0;

            var names = new List<string>();
            using (var gz  = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true))
            using (var tar = new TarReader(gz))
            {
                TarEntry? entry;
                while ((entry = tar.GetNextEntry(copyData: true)) is not null)
                    names.Add(entry.Name);
            }
            ms.Position = 0;

            string templateEntry = names.First(n => n.EndsWith(".template.template.xml"));
            string xml = ReadTarEntry(ms, templateEntry)!;

            Assert.Contains("/application/themes/TestTheme/Images/logo.png", xml);
            // The generic /logo.png path (not under the theme) must not appear.
            Assert.DoesNotContain("src=&quot;/logo.png&quot;", xml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithTheme_PortalLogoFileIsCopiedToThemeImagesDir()
    {
        // When the themes zip does NOT contain an Images/logo.png but a
        // portal file exists with that name in the Images/ folder, the
        // bundle writer must copy it into the theme's Images/ directory
        // so that the <dnn:LOGO> → <img> replacement resolves correctly.
        string zipPath = Path.GetTempFileName() + ".zip";
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry skin = zip.CreateEntry("_default/Skins/TestTheme/Home.ascx");
                using (var w = new StreamWriter(skin.Open()))
                    w.Write("""<div><dnn:LOGO runat="server" id="logo" /></div>""");

                ZipArchiveEntry container = zip.CreateEntry("_default/Containers/TestTheme/C.ascx");
                using (var w = new StreamWriter(container.Open()))
                    w.Write("""<div id="ContentPane" runat="server"></div>""");

                // Notably, NO Images/logo.png in the theme zip.
            }

            byte[] logoBytes = [0x89, 0x50, 0x4E, 0x47]; // minimal PNG header
            var portalFiles = new List<DnnPortalFile>
            {
                new("uid-logo", "ver-logo", "logo.png", "Images/", "image/png", logoBytes)
            };

            var (ms, names) = WriteBundleWithFilesAndThemes(portalFiles, zipPath, "Test Site");

            // The theme binary asset for logo.png must exist (pattern: assets/…/logo.png).
            // The logo should appear exactly once — in the theme directory only.
            var logoAssetEntries = names.Where(n =>
                n.StartsWith("assets/") && n.EndsWith("/logo.png")).ToList();
            Assert.True(logoAssetEntries.Count >= 1,
                $"Expected at least 1 logo.png asset entry (theme), found {logoAssetEntries.Count}");

            // Verify the theme copy's contentlet XML references the correct folder path.
            var contentEntries = names.Where(n =>
                n.StartsWith("live/") && n.EndsWith(".content.xml")).ToList();
            bool foundThemeLogo = false;
            foreach (string ce in contentEntries)
            {
                string? xml = ReadTarEntry(ms, ce);
                if (xml is not null &&
                    xml.Contains("application/themes/TestTheme/Images/") &&
                    xml.Contains("logo.png"))
                {
                    foundThemeLogo = true;
                    break;
                }
            }
            Assert.True(foundThemeLogo,
                "Expected a contentlet XML referencing application/themes/TestTheme/Images/logo.png");

            // The portal logo file must be consumed (placed only in the theme
            // directory) and NOT duplicated at the site root.
            Assert.DoesNotContain(names, n => n == "ROOT/Images/logo.png");
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithTheme_PortalRootLogoFileIsCopiedToThemeImagesDir()
    {
        // When logo.png lives at the portal ROOT (FolderPath = "") instead
        // of the Images/ sub-folder, it must still be copied into the
        // theme's Images/ directory and consumed (not duplicated at ROOT/).
        string zipPath = Path.GetTempFileName() + ".zip";
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry skin = zip.CreateEntry("_default/Skins/TestTheme/Home.ascx");
                using (var w = new StreamWriter(skin.Open()))
                    w.Write("""<div><dnn:LOGO runat="server" id="logo" /></div>""");

                ZipArchiveEntry container = zip.CreateEntry("_default/Containers/TestTheme/C.ascx");
                using (var w = new StreamWriter(container.Open()))
                    w.Write("""<div id="ContentPane" runat="server"></div>""");
            }

            byte[] logoBytes = [0x89, 0x50, 0x4E, 0x47]; // minimal PNG header
            var portalFiles = new List<DnnPortalFile>
            {
                // logo.png at the portal root – no Images/ folder
                new("uid-logo", "ver-logo", "logo.png", "", "image/png", logoBytes)
            };

            var (ms, names) = WriteBundleWithFilesAndThemes(portalFiles, zipPath, "Test Site");

            // The theme copy must exist.
            var logoAssetEntries = names.Where(n =>
                n.StartsWith("assets/") && n.EndsWith("/logo.png")).ToList();
            Assert.True(logoAssetEntries.Count >= 1,
                $"Expected at least 1 logo.png asset entry (theme), found {logoAssetEntries.Count}");

            // Verify the theme copy's contentlet XML references the correct path.
            var contentEntries = names.Where(n =>
                n.StartsWith("live/") && n.EndsWith(".content.xml")).ToList();
            bool foundThemeLogo = false;
            foreach (string ce in contentEntries)
            {
                string? xml = ReadTarEntry(ms, ce);
                if (xml is not null &&
                    xml.Contains("application/themes/TestTheme/Images/") &&
                    xml.Contains("logo.png"))
                {
                    foundThemeLogo = true;
                    break;
                }
            }
            Assert.True(foundThemeLogo,
                "Expected a contentlet XML referencing application/themes/TestTheme/Images/logo.png");

            // The portal logo must be consumed – NOT at the site root.
            Assert.DoesNotContain(names, n => n == "ROOT/logo.png");
        }
        finally { File.Delete(zipPath); }
    }

    // ------------------------------------------------------------------
    // Theme zip – all non-ASCX file types included
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithThemesZip_NonStaticExtensionFilesAreIncluded()
    {
        // Essential theme files such as .html, .xml, and .json must be
        // included in the bundle — not silently dropped because they are not
        // in the StaticExtensions list.
        string zipPath = Path.GetTempFileName() + ".zip";
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                // HTML layout file
                ZipArchiveEntry html = zip.CreateEntry("_default/Skins/MyTheme/layout.html");
                using (var w = new StreamWriter(html.Open()))
                    w.Write("<html><body></body></html>");

                // XML config file
                ZipArchiveEntry xml = zip.CreateEntry("_default/Skins/MyTheme/theme.xml");
                using (var w = new StreamWriter(xml.Open()))
                    w.Write("<theme><name>MyTheme</name></theme>");

                // JSON config file
                ZipArchiveEntry json = zip.CreateEntry("_default/Skins/MyTheme/theme.json");
                using (var w = new StreamWriter(json.Open()))
                    w.Write("{}");

                // Minimal ASCX stub (should be skipped – handled by CollectThemeDefinitions)
                ZipArchiveEntry ascx = zip.CreateEntry("_default/Skins/MyTheme/Home.ascx");
                using (var w = new StreamWriter(ascx.Open()))
                    w.Write("""<div id="ContentPane" runat="server"></div>""");
            }

            var (_, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);

            Assert.Contains(names, n => n.EndsWith("layout.html"));
            Assert.Contains(names, n => n.EndsWith("theme.xml"));
            Assert.Contains(names, n => n.EndsWith("theme.json"));
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithThemesZip_AscxFilesNotWrittenAsRawFiles()
    {
        // ASCX files are processed by CollectThemeDefinitions and converted to
        // container/template XML.  They must NOT also appear as raw .ascx entries.
        string zipPath = BuildThemesZip();
        try
        {
            var (_, names) = WriteBundleToMemory([MakeHtmlContentType()], zipPath);

            Assert.DoesNotContain(names, n => n.EndsWith(".ascx"));
        }
        finally { File.Delete(zipPath); }
    }

    // ------------------------------------------------------------------
    // Images/ folder – FolderPath without trailing slash
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithPortalFiles_ImagesFolderPathWithoutSlashAlsoWrittenToRootImages()
    {
        // DNN may store the folder path as "Images" (no trailing slash) in some
        // export versions.  The bundle writer must still place such files under
        // ROOT/Images/ so that converted HTML references resolve.
        var files = new[]
        {
            new DnnPortalFile(
                "6f574d5f-0880-4d5a-b4a2-74d2e10b5659",
                "69f363b0-6512-48ad-b187-b6a450ffda7b",
                "logo.png", "Images",   // ← no trailing slash
                "image/png",
                [0x89, 0x50, 0x4E, 0x47]),
        };

        var (_, names) = WriteBundleWithFiles(files);

        Assert.Contains("ROOT/Images/logo.png", names);
    }

    [Fact]
    public void Write_WithPortalFiles_ImagesFolderSubdirPathWithoutSlashPreservesSubdir()
    {
        // Same normalisation for nested paths: "Images/Banners" (no trailing slash)
        // must still produce ROOT/Images/Banners/{filename}.
        var files = new[]
        {
            new DnnPortalFile(
                "aaaaaaaa-0000-0000-0000-000000000002",
                "bbbbbbbb-0000-0000-0000-000000000002",
                "hero.jpg", "Images/Banners",   // ← no trailing slash
                "image/jpeg",
                [0xFF, 0xD8, 0xFF]),
        };

        var (_, names) = WriteBundleWithFiles(files);

        Assert.Contains("ROOT/Images/Banners/hero.jpg", names);
    }

    // ------------------------------------------------------------------
    // Nested portal folders – parent folder hierarchy
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithPortalFiles_NestedFolderCreatesIntermediateParentFolderEntries()
    {
        // When a portal file lives in a nested folder like "Menus/SubFolder/",
        // the bundle must include FolderWrapper XML entries for both "Menus/"
        // (the intermediate parent) and "Menus/SubFolder/" (the leaf).
        var files = new[]
        {
            new DnnPortalFile(
                "11111111-0000-0000-0000-000000000001",
                "22222222-0000-0000-0000-000000000001",
                "nav.xml", "Menus/SubFolder/", "text/xml",
                Encoding.UTF8.GetBytes("<nav/>")),
        };

        var (ms, names) = WriteBundleWithFiles(files, "Test Site");

        // Must have at least 2 folder XML entries: one for Menus/ and one for Menus/SubFolder/.
        var folderEntries = names.Where(n => n.StartsWith("ROOT/") && n.EndsWith(".folder.xml")).ToList();
        Assert.True(folderEntries.Count >= 2,
            $"Expected at least 2 folder entries, got {folderEntries.Count}: {string.Join(", ", folderEntries)}");

        // Verify the parent folder (Menus/) exists with parentPath="/".
        bool foundParent = false;
        foreach (string entry in folderEntries)
        {
            string xml = ReadTarEntry(ms, entry)!;
            if (xml.Contains("<name>Menus</name>") && xml.Contains("<path>/Menus/</path>"))
            {
                Assert.Contains("<parentPath>/</parentPath>", xml);
                foundParent = true;
            }
        }
        Assert.True(foundParent, "Missing FolderWrapper entry for intermediate parent folder 'Menus'.");
    }

    [Fact]
    public void Write_WithPortalFiles_NestedFolderHasCorrectParentPath()
    {
        // The child folder "Menus/SubFolder/" must reference parentPath="/Menus/"
        // (not "/") so DotCMS's identifier_parent_path_check constraint is satisfied.
        var files = new[]
        {
            new DnnPortalFile(
                "11111111-0000-0000-0000-000000000001",
                "22222222-0000-0000-0000-000000000001",
                "nav.xml", "Menus/SubFolder/", "text/xml",
                Encoding.UTF8.GetBytes("<nav/>")),
        };

        var (ms, names) = WriteBundleWithFiles(files, "Test Site");

        var folderEntries = names.Where(n => n.StartsWith("ROOT/") && n.EndsWith(".folder.xml")).ToList();

        bool foundChild = false;
        foreach (string entry in folderEntries)
        {
            string xml = ReadTarEntry(ms, entry)!;
            if (xml.Contains("<name>SubFolder</name>") && xml.Contains("<path>/Menus/SubFolder/</path>"))
            {
                Assert.Contains("<parentPath>/Menus/</parentPath>", xml);
                foundChild = true;
            }
        }
        Assert.True(foundChild, "Missing FolderWrapper entry for nested folder 'Menus/SubFolder' with correct parentPath.");
    }

    [Fact]
    public void Write_WithPortalFiles_DeeplyNestedFolderCreatesAllIntermediateFolders()
    {
        // A 3-level deep folder like "A/B/C/" must produce folder entries for
        // "A/", "A/B/", and "A/B/C/" with correct parent paths.
        var files = new[]
        {
            new DnnPortalFile(
                "33333333-0000-0000-0000-000000000001",
                "44444444-0000-0000-0000-000000000001",
                "deep.txt", "A/B/C/", "text/plain",
                Encoding.UTF8.GetBytes("deep")),
        };

        var (ms, names) = WriteBundleWithFiles(files, "Test Site");

        var folderEntries = names.Where(n => n.StartsWith("ROOT/") && n.EndsWith(".folder.xml")).ToList();
        Assert.True(folderEntries.Count >= 3,
            $"Expected at least 3 folder entries for A/B/C/, got {folderEntries.Count}");

        // Collect all folder XMLs for inspection.
        var folderXmls = folderEntries.Select(e => ReadTarEntry(ms, e)!).ToList();

        // A/ → parentPath="/"
        Assert.Contains(folderXmls, xml =>
            xml.Contains("<name>A</name>") && xml.Contains("<parentPath>/</parentPath>"));
        // A/B/ → parentPath="/A/"
        Assert.Contains(folderXmls, xml =>
            xml.Contains("<name>B</name>") && xml.Contains("<parentPath>/A/</parentPath>"));
        // A/B/C/ → parentPath="/A/B/"
        Assert.Contains(folderXmls, xml =>
            xml.Contains("<name>C</name>") && xml.Contains("<parentPath>/A/B/</parentPath>"));
    }

    // ------------------------------------------------------------------
    // Folder XML nesting – DotCMS FolderHandler path-length sort
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithPortalFiles_RootFolderXmlEntryIsDirectlyUnderRoot()
    {
        // A top-level portal folder (parentPath="/") must have its .folder.xml
        // entry directly under ROOT/ so DotCMS's sort-by-path-length processes
        // it before any nested children.
        var files = new[]
        {
            new DnnPortalFile(
                "11111111-0000-0000-0000-000000000001",
                "22222222-0000-0000-0000-000000000001",
                "logo.png", "Images/", "image/png",
                Encoding.UTF8.GetBytes("img")),
        };

        var (_, names) = WriteBundleWithFiles(files, "Test Site");

        var folderEntry = names.First(n => n.StartsWith("ROOT/") && n.EndsWith(".folder.xml"));
        // Should be ROOT/{uuid}.folder.xml — only one slash after ROOT
        int slashCount = folderEntry.Count(c => c == '/');
        Assert.Equal(1, slashCount);
    }

    [Fact]
    public void Write_WithPortalFiles_NestedFolderXmlEntriesHaveIncreasingPathDepth()
    {
        // For nested folders like "A/B/C/", the .folder.xml entries must be
        // placed in progressively deeper directories so DotCMS's FolderHandler
        // (which sorts by file-path length) processes parents before children.
        //   A/ → ROOT/{uuid}.folder.xml
        //   A/B/ → ROOT/A/{uuid}.folder.xml
        //   A/B/C/ → ROOT/A/B/{uuid}.folder.xml
        var files = new[]
        {
            new DnnPortalFile(
                "33333333-0000-0000-0000-000000000001",
                "44444444-0000-0000-0000-000000000001",
                "deep.txt", "A/B/C/", "text/plain",
                Encoding.UTF8.GetBytes("deep")),
        };

        var (ms, names) = WriteBundleWithFiles(files, "Test Site");

        var folderEntries = names.Where(n => n.StartsWith("ROOT/") && n.EndsWith(".folder.xml")).ToList();
        Assert.True(folderEntries.Count >= 3,
            $"Expected at least 3 folder entries, got {folderEntries.Count}");

        // Sort by path length (as DotCMS does) and verify parents come first.
        var sorted = folderEntries.OrderBy(n => n.Length).ToList();
        var xmls = sorted.Select(e => ReadTarEntry(ms, e)!).ToList();

        // First (shortest): folder A, under ROOT/
        Assert.Contains("<name>A</name>", xmls[0]);
        Assert.StartsWith("ROOT/", sorted[0]);
        Assert.DoesNotContain("/A/", sorted[0]);  // no nested dir

        // Second: folder B, under ROOT/A/
        Assert.Contains("<name>B</name>", xmls[1]);
        Assert.StartsWith("ROOT/A/", sorted[1]);

        // Third (longest): folder C, under ROOT/A/B/
        Assert.Contains("<name>C</name>", xmls[2]);
        Assert.StartsWith("ROOT/A/B/", sorted[2]);
    }

    [Fact]
    public void Write_WithPages_PageXmlContainsModDate()
    {
        // DotCMS uses modDate from the contentlet map during push-publish
        // import to preserve the modification timestamp.  Pages must include
        // it just like content and file-asset contentlets do.
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Home", "Home", "", "//Home", 0, true, ""),
        };

        var (ms, names) = WriteBundleWithPages(pages);
        string entryName = names.First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml"));
        string xml = ReadTarEntry(ms, entryName)!;

        Assert.Contains("<string>modDate</string>", xml);
        Assert.Contains("<sql-timestamp>", xml);
    }

    [Fact]
    public void Write_WithPortalFiles_FolderXmlContainsDefaultFileType()
    {
        // DotCMS requires the folder's defaultFileType to reference the
        // FileAsset content type so that file browser operations work
        // correctly.  An empty value can cause folder creation to fail.
        var files = new[]
        {
            new DnnPortalFile(
                "6f574d5f-0880-4d5a-b4a2-74d2e10b5659",
                "69f363b0-6512-48ad-b187-b6a450ffda7b",
                "logo.png", "Images/", "image/png",
                [0x00]),
        };

        var (ms, names) = WriteBundleWithFiles(files, "Test Site");

        string folderEntry = names.First(n => n.StartsWith("ROOT/") && n.EndsWith(".folder.xml"));
        string xml = ReadTarEntry(ms, folderEntry)!;

        // The FileAsset content type UUID.
        Assert.Contains("<defaultFileType>33888b6f-7a8e-4069-b1b6-5c1aa9d0a48d</defaultFileType>", xml);
    }

    // ------------------------------------------------------------------
    // Portal file → theme resolution tests
    // ------------------------------------------------------------------

    /// <summary>
    /// Write a bundle with both a themes zip and portal files.
    /// </summary>
    private static (MemoryStream stream, List<string> entryNames) WriteBundleWithFilesAndThemes(
        IReadOnlyList<DnnPortalFile> files,
        string themesZipPath,
        string siteName = "Test Site")
    {
        var ms = new MemoryStream();
        BundleWriter.Write([MakeHtmlContentType()], ms, themesZipPath, siteName, null, null, files);
        ms.Position = 0;

        var names = new List<string>();
        using var gz  = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gz);

        TarEntry? entry;
        while ((entry = tar.GetNextEntry(copyData: true)) is not null)
            names.Add(entry.Name);

        ms.Position = 0;
        return (ms, names);
    }

    /// <summary>Read raw bytes of a specific tar entry.</summary>
    private static byte[]? ReadTarEntryBytes(MemoryStream bundleStream, string entryName)
    {
        bundleStream.Position = 0;
        using var gz  = new GZipStream(bundleStream, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gz);

        TarEntry? entry;
        while ((entry = tar.GetNextEntry(copyData: true)) is not null)
        {
            if (entry.Name == entryName && entry.DataStream is not null)
            {
                using var ms2 = new MemoryStream();
                entry.DataStream.CopyTo(ms2);
                return ms2.ToArray();
            }
        }
        return null;
    }

    [Fact]
    public void Write_WithPortalCssAndThemes_ResolvesPerSkinCssFromPortalFiles()
    {
        // Theme zip has Home.ascx but no Home.css.
        // Portal files have home.css at site root.
        // Expected: the Home.css asset in the theme folder should contain
        // the portal file's real content, not a placeholder.
        string zipPath = BuildThemesZip();
        try
        {
            byte[] realCssContent = Encoding.UTF8.GetBytes("body { margin: 0; font-size: 14px; }");
            var files = new[]
            {
                new DnnPortalFile(
                    "e5dfe1f2-4cdc-46bd-ad32-7257a6b8105a",
                    "2af85195-c192-4a33-a14d-a8bb2dc6007e",
                    "Home.css", "", "text/css",
                    realCssContent),
            };

            var (ms, names) = WriteBundleWithFilesAndThemes(files, zipPath);

            // A theme binary asset for Home.css must exist.
            string assetEntry = names.First(n =>
                n.StartsWith("assets/") && n.EndsWith("/Home.css"));
            byte[]? assetBytes = ReadTarEntryBytes(ms, assetEntry);

            Assert.NotNull(assetBytes);
            Assert.Equal(realCssContent, assetBytes);
            Assert.DoesNotContain("placeholder", Encoding.UTF8.GetString(assetBytes));
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithPortalCssAndThemes_ExcludesConsumedPortalFileFromSiteRoot()
    {
        // When a portal file is consumed by theme resolution, it must NOT
        // also be written at the site root (SYSTEM_FOLDER).
        string zipPath = BuildThemesZip();
        try
        {
            byte[] cssContent = Encoding.UTF8.GetBytes("body { color: red; }");
            var files = new[]
            {
                new DnnPortalFile(
                    "e5dfe1f2-4cdc-46bd-ad32-7257a6b8105a",
                    "2af85195-c192-4a33-a14d-a8bb2dc6007e",
                    "Home.css", "", "text/css",
                    cssContent),
            };

            var (ms, names) = WriteBundleWithFilesAndThemes(files, zipPath, "Test Site");

            // The portal file's unique identifier should NOT appear in any
            // content.xml entry at the site root (it was consumed by the
            // theme).  Look for the portal file's UniqueId in content XML.
            var portalContentEntries = names.Where(n =>
                n.Contains("/1/") && n.EndsWith(".content.xml") &&
                !n.Contains("host.xml"));
            foreach (string entry in portalContentEntries)
            {
                string xml = ReadTarEntry(ms, entry)!;
                // If this entry contains the portal file's unique ID, it was
                // written for the site root — which should not happen.
                if (xml.Contains("e5dfe1f2-4cdc-46bd-ad32-7257a6b8105a"))
                {
                    // Verify it's in the theme folder, not SYSTEM_FOLDER
                    Assert.DoesNotContain("<folder>SYSTEM_FOLDER</folder>", xml);
                }
            }
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithPortalCssAndThemes_NonMatchingPortalFilesStayAtSiteRoot()
    {
        // Portal files that DON'T match any per-skin CSS should still be
        // written at the site root as before.
        string zipPath = BuildThemesZip();
        try
        {
            var files = new[]
            {
                new DnnPortalFile(
                    "aaaaaaaa-bbbb-cccc-dddd-000000000001",
                    "11111111-2222-3333-4444-555555555555",
                    "custom.js", "", "application/javascript",
                    Encoding.UTF8.GetBytes("console.log('hello');")),
            };

            var (ms, names) = WriteBundleWithFilesAndThemes(files, zipPath, "Test Site");

            // custom.js should still be written as a binary asset at the site root.
            Assert.Contains(names, n => n.StartsWith("assets/") && n.EndsWith("/custom.js"));
        }
        finally { File.Delete(zipPath); }
    }

    // ------------------------------------------------------------------
    // Dynamic portal file linking – non-Images folders written to ROOT/
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithPortalFiles_PdfFolderFilesWrittenToRoot()
    {
        // Files in non-Images folders like PDFs/ must be written as static
        // resources under ROOT/{folderPath}/ so that HTML references like
        // {{PortalRoot}}PDFs/doc.pdf → /PDFs/doc.pdf resolve correctly.
        var files = new[]
        {
            new DnnPortalFile(
                "11111111-aaaa-bbbb-cccc-000000000001",
                "22222222-aaaa-bbbb-cccc-000000000001",
                "doc.pdf", "PDFs/", "application/pdf",
                Encoding.UTF8.GetBytes("%PDF-1.4")),
        };

        var (_, names) = WriteBundleWithFiles(files);

        Assert.Contains("ROOT/PDFs/doc.pdf", names);
    }

    [Fact]
    public void Write_WithPortalFiles_CustomImageFolderWrittenToRoot()
    {
        // Folders with custom names (e.g. FisSlider-Images/) that don't start
        // with Images/ must still be written as static resources under ROOT/.
        var files = new[]
        {
            new DnnPortalFile(
                "11111111-aaaa-bbbb-cccc-000000000002",
                "22222222-aaaa-bbbb-cccc-000000000002",
                "slide.jpg", "FisSlider-Images/", "image/jpeg",
                [0xFF, 0xD8, 0xFF]),
        };

        var (_, names) = WriteBundleWithFiles(files);

        Assert.Contains("ROOT/FisSlider-Images/slide.jpg", names);
    }

    [Fact]
    public void Write_WithPortalFiles_RootLevelFilesWrittenToRoot()
    {
        // Root-level portal files (FolderPath = "") should be written to ROOT/
        // so that {{PortalRoot}}file.css → /file.css resolves correctly.
        var files = new[]
        {
            new DnnPortalFile(
                "11111111-aaaa-bbbb-cccc-000000000003",
                "22222222-aaaa-bbbb-cccc-000000000003",
                "home.css", "", "text/css",
                Encoding.UTF8.GetBytes("body { color: red; }")),
        };

        var (_, names) = WriteBundleWithFiles(files);

        Assert.Contains("ROOT/home.css", names);
    }

    [Fact]
    public void Write_WithPortalFiles_ContainersFolderNotWrittenToRoot()
    {
        // Files in Containers/ are theme-related (handled by export_themes.zip)
        // and must NOT be written as static resources under ROOT/.
        var files = new[]
        {
            new DnnPortalFile(
                "11111111-aaaa-bbbb-cccc-000000000004",
                "22222222-aaaa-bbbb-cccc-000000000004",
                "Boxed.ascx", "Containers/FBOT/", "text/plain",
                Encoding.UTF8.GetBytes("<div>container</div>")),
        };

        var (_, names) = WriteBundleWithFiles(files);

        Assert.DoesNotContain(names, n => n == "ROOT/Containers/FBOT/Boxed.ascx");
        // Must still be written as a FileAsset contentlet (binary + XML).
        Assert.Contains(names, n => n.StartsWith("assets/") && n.EndsWith("/Boxed.ascx"));
    }

    [Fact]
    public void Write_WithPortalFiles_SkinsFolderNotWrittenToRoot()
    {
        // Files in Skins/ are theme-related and must NOT be written as static
        // resources under ROOT/.
        var files = new[]
        {
            new DnnPortalFile(
                "11111111-aaaa-bbbb-cccc-000000000005",
                "22222222-aaaa-bbbb-cccc-000000000005",
                "skin.css", "Skins/FBOT/", "text/css",
                Encoding.UTF8.GetBytes("/* skin */")),
        };

        var (_, names) = WriteBundleWithFiles(files);

        Assert.DoesNotContain(names, n => n == "ROOT/Skins/FBOT/skin.css");
    }

    [Fact]
    public void Write_WithPortalFiles_FolderWithoutSlashStillWrittenToRoot()
    {
        // DNN may omit the trailing slash from folder paths.  The normalisation
        // must still produce the correct ROOT/ entry.
        var files = new[]
        {
            new DnnPortalFile(
                "11111111-aaaa-bbbb-cccc-000000000006",
                "22222222-aaaa-bbbb-cccc-000000000006",
                "report.pdf", "PDFs",   // ← no trailing slash
                "application/pdf",
                Encoding.UTF8.GetBytes("%PDF-1.5")),
        };

        var (_, names) = WriteBundleWithFiles(files);

        Assert.Contains("ROOT/PDFs/report.pdf", names);
    }

    // ----- ExtractThemeNameFromSkinSrc tests -----

    [Theory]
    [InlineData("[G]Skins/FidelityBankTexas/Home.ascx", "FidelityBankTexas")]
    [InlineData("[L]Skins/Cavalier/Inner.ascx", "Cavalier")]
    [InlineData("[G]Skins/Xcillion/Home.ascx", "Xcillion")]
    [InlineData("[G]Skins\\Xcillion\\Home.ascx", "Xcillion")] // backslash paths
    public void ExtractThemeNameFromSkinSrc_ReturnsThemeName(string skinSrc, string expected)
    {
        string? result = BundleWriter.ExtractThemeNameFromSkinSrc(skinSrc);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Home.ascx")]
    [InlineData("[G]Containers/Something/Box.ascx")]
    public void ExtractThemeNameFromSkinSrc_ReturnsNull_WhenNoSkinsSegment(string skinSrc)
    {
        Assert.Null(BundleWriter.ExtractThemeNameFromSkinSrc(skinSrc));
    }

    // ------------------------------------------------------------------
    // PaneUuidMap tests
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertAscxToTemplateHtml_ReturnsPaneUuidMap_ForMultiPaneSkin()
    {
        // A DNN skin with several named panes (header, content, footer zones)
        // must produce a PaneUuidMap that maps each div id to its sequential
        // #parseContainer UUID slot number.
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div id="HeaderPane" runat="server"></div>
            <div id="ContentPane" runat="server"></div>
            <div id="FooterLeft" runat="server"></div>
            <div id="FooterRight" runat="server"></div>
            """;

        var (body, _, paneUuidMap) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, defaultContainerId: "container-1");

        // All four pane ids must appear in the mapping.
        Assert.Equal(4, paneUuidMap.Count);
        Assert.True(paneUuidMap.ContainsKey("HeaderPane"));
        Assert.True(paneUuidMap.ContainsKey("ContentPane"));
        Assert.True(paneUuidMap.ContainsKey("FooterLeft"));
        Assert.True(paneUuidMap.ContainsKey("FooterRight"));

        // UUIDs must be sequential starting at 1.
        Assert.Equal(1, paneUuidMap["HeaderPane"]);
        Assert.Equal(2, paneUuidMap["ContentPane"]);
        Assert.Equal(3, paneUuidMap["FooterLeft"]);
        Assert.Equal(4, paneUuidMap["FooterRight"]);

        // The template body must contain matching #parseContainer directives.
        Assert.Contains("#parseContainer('container-1', '1')", body);
        Assert.Contains("#parseContainer('container-1', '2')", body);
        Assert.Contains("#parseContainer('container-1', '3')", body);
        Assert.Contains("#parseContainer('container-1', '4')", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_PaneUuidMapEmpty_WhenNoContainerId()
    {
        // When no container id is provided, pane divs are removed and no
        // pane mapping is generated.
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div id="ContentPane" runat="server"></div>
            <div id="FooterPane" runat="server"></div>
            """;

        var (_, _, paneUuidMap) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Empty(paneUuidMap);
    }

    // ------------------------------------------------------------------
    // dnn_ ID prefix tests
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertAscxToTemplateHtml_PaneDivIdGetsDnnPrefix()
    {
        // Pane divs with runat="server" should get the dnn_ prefix on
        // their id attribute to match the rendered DNN page.
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div class="col-md" id="FDIC" runat="server"></div>
            """;

        var (body, _, paneUuidMap) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, "ctr1");

        Assert.Contains("id=\"dnn_FDIC\"", body);
        Assert.DoesNotContain("id=\"FDIC\"", body);
        // paneUuidMap still uses the original (unprefixed) id for content mapping.
        Assert.True(paneUuidMap.ContainsKey("FDIC"));
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_MultiplePaneDivsAllGetDnnPrefix()
    {
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div class="top-bar-bg">
              <div class="row">
                <div class="col-md" id="FDIC" runat="server"></div>
                <div class="col-md" id="TopRightBar" runat="server"></div>
              </div>
            </div>
            <div class="col-xl-2" id="login" runat="server"></div>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, "ctr1");

        Assert.Contains("id=\"dnn_FDIC\"", body);
        Assert.Contains("id=\"dnn_TopRightBar\"", body);
        Assert.Contains("id=\"dnn_login\"", body);
    }

    [Fact]
    public void ConvertAscxToContainerHtml_RunatServerDivGetsDnnPrefix()
    {
        const string ascx = """<div id="Wrapper" runat="server"><p>text</p></div>""";

        string result = BundleWriter.ConvertAscxToContainerHtml(ascx);

        Assert.Contains("id=\"dnn_Wrapper\"", result);
        Assert.DoesNotContain("id=\"Wrapper\"", result);
        Assert.DoesNotContain("runat", result);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_NonRunatDivsNotPrefixed()
    {
        // Divs without runat="server" should NOT get the dnn_ prefix.
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div id="header" class="sticky-top">
              <div id="ContentPane" runat="server"></div>
            </div>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(
            ascx, "ctr1");

        // "header" does not have runat="server" so no prefix.
        Assert.Contains("id=\"header\"", body);
        Assert.DoesNotContain("id=\"dnn_header\"", body);
    }

    // ------------------------------------------------------------------
    // Default container selection tests
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveDefaultContainerId_PrefersStandard()
    {
        var defs = new List<(string id, string inode, string name, string html, string themeName)>
        {
            ("aaa", "i1", "Accountcard", "<div>acc</div>", "theme1"),
            ("bbb", "i2", "standard", "<div>std</div>", "theme1"),
            ("ccc", "i3", "hpcard", "<div>hp</div>", "theme1"),
        };

        string result = BundleWriter.ResolveDefaultContainerId(defs);

        Assert.Equal("bbb", result);
    }

    [Fact]
    public void ResolveDefaultContainerId_FallsBackToFirst_WhenNoStandard()
    {
        var defs = new List<(string id, string inode, string name, string html, string themeName)>
        {
            ("aaa", "i1", "Accountcard", "<div>acc</div>", "theme1"),
            ("ccc", "i2", "hpcard", "<div>hp</div>", "theme1"),
        };

        string result = BundleWriter.ResolveDefaultContainerId(defs);

        Assert.Equal("aaa", result);
    }

    [Fact]
    public void ResolveDefaultContainerId_ReturnsEmpty_WhenNoDefs()
    {
        var defs = new List<(string id, string inode, string name, string html, string themeName)>();

        string result = BundleWriter.ResolveDefaultContainerId(defs);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ResolveDefaultContainerId_CaseInsensitive()
    {
        var defs = new List<(string id, string inode, string name, string html, string themeName)>
        {
            ("aaa", "i1", "Accountcard", "<div>acc</div>", "theme1"),
            ("bbb", "i2", "Standard", "<div>std</div>", "theme1"),
        };

        string result = BundleWriter.ResolveDefaultContainerId(defs);

        Assert.Equal("bbb", result);
    }

    // ------------------------------------------------------------------
    // Footer control replacement tests (COPYRIGHT, TERMS, PRIVACY)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertAscxToTemplateHtml_CopyrightRendersVisibleHtml()
    {
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div class="footer"><dnn:COPYRIGHT ID="c1" runat="server" /></div>
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains("Copyright", body);
        Assert.DoesNotContain("<!-- Copyright -->", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_TermsRendersVisibleLink()
    {
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <dnn:TERMS ID="t1" runat="server" />
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains("Terms of Use", body);
        Assert.Contains("href=", body);
        Assert.DoesNotContain("<!-- Terms", body);
    }

    [Fact]
    public void ConvertAscxToTemplateHtml_PrivacyRendersVisibleLink()
    {
        const string ascx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <dnn:PRIVACY ID="p1" runat="server" />
            """;

        var (body, _, _) = BundleWriter.ConvertAscxToTemplateHtml(ascx);

        Assert.Contains("Privacy", body);
        Assert.Contains("href=", body);
        Assert.DoesNotContain("<!-- Privacy", body);
    }

    [Fact]
    public void Write_WithFooterPaneContent_MultiTreeRelationTypeMatchesFooterSlot()
    {
        // Footer modules placed in high-numbered pane slots (e.g. slot 5 for
        // FooterLeft in a skin with 5 runat="server" divs) must get the
        // correct relation_type in multiTree so DotCMS renders the content
        // in the matching #parseContainer directive.
        string tabId = "footer-pane-test";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "Home", "Home", "", "//Home", 0, true, ""),
        };
        // Skin with content panes followed by 4 footer panes — mimics a
        // real DNN skin like Xcillion Home.ascx.
        string skinAscx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div id="HeaderPane" runat="server"></div>
            <div id="ContentPane" runat="server"></div>
            <div id="SidePane" runat="server"></div>
            <footer>
                <div id="FooterLeft" runat="server"></div>
                <div id="FooterLeftCenter" runat="server"></div>
                <div id="FooterRightCenter" runat="server"></div>
                <div id="FooterRight" runat="server"></div>
            </footer>
            """;
        // Content module lives in FooterLeft (slot 4) and FooterRight (slot 7).
        var contents = new[]
        {
            new DnnHtmlContent("Footer 1", "<p>about</p>", TabUniqueId: tabId, PaneName: "FooterLeft"),
            new DnnHtmlContent("Footer 4", "<p>logo</p>",  TabUniqueId: tabId, PaneName: "FooterRight"),
        };

        string zipPath = BuildThemesZip(skinAscx: skinAscx);
        try
        {
            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents,
                themesZipPath: zipPath);

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
            // FooterLeft is the 4th runat="server" div → slot 4.
            // FooterRight is the 7th runat="server" div → slot 7.
            int idx1 = pageXml!.IndexOf("<string>relation_type</string>", StringComparison.Ordinal);
            Assert.True(idx1 >= 0);
            string afterFirst = pageXml[(idx1 + "<string>relation_type</string>".Length)..];
            Assert.Contains("<string>4</string>", afterFirst);

            int idx2 = pageXml.IndexOf("<string>relation_type</string>", idx1 + 1, StringComparison.Ordinal);
            Assert.True(idx2 >= 0);
            string afterSecond = pageXml[(idx2 + "<string>relation_type</string>".Length)..];
            Assert.Contains("<string>7</string>", afterSecond);
        }
        finally { File.Delete(zipPath); }
    }

    // ------------------------------------------------------------------
    // showOnMenu / navigation tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithVisiblePage_PageXmlHasShowOnMenuTrue()
    {
        string tabId = "visible-page-test";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "About", "About Us", "", "//About", 0, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("About", "<p>About</p>", TabUniqueId: tabId),
        };

        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents,
                themesZipPath: zipPath);

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
            Assert.Contains("<string>showOnMenu</string>", pageXml);
            Assert.Contains("<boolean>true</boolean>", pageXml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithHiddenPage_PageXmlHasShowOnMenuFalse()
    {
        string tabId = "hidden-page-test";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "Search", "Search Results", "", "//Search", 0, false, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Search", "<p>Search</p>", TabUniqueId: tabId),
        };

        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents,
                themesZipPath: zipPath);

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
            Assert.Contains("<string>showOnMenu</string>", pageXml);
            Assert.Contains("<boolean>false</boolean>", pageXml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithTabOrder_PageXmlHasSortOrder()
    {
        string tabId = "sort-order-test";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "About", "About Us", "", "//About", 0, true, "", TabOrder: 5),
        };
        var contents = new[]
        {
            new DnnHtmlContent("About", "<p>About</p>", TabUniqueId: tabId),
        };

        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents,
                themesZipPath: zipPath);

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
            Assert.Contains("<long>5</long>", pageXml);
        }
        finally { File.Delete(zipPath); }
    }

    // ------------------------------------------------------------------
    // Child page / folder hierarchy tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithChildPage_CreatesParentFolder()
    {
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Personal", "Personal", "", "//Personal",  0, true, ""),
            new DnnPortalPage("bbb", "Checking", "Checking", "", "//Personal//Checking", 1, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Personal", "<p>Personal</p>", TabUniqueId: "aaa"),
            new DnnHtmlContent("Checking", "<p>Checking</p>", TabUniqueId: "bbb"),
        };

        string zipPath = BuildThemesZip();
        try
        {
            var (_, names) = WriteBundleWithPagesAndContents(pages, contents,
                themesZipPath: zipPath);

            // A .folder.xml entry should be created for the "personal" folder.
            Assert.Contains(names, n => n.EndsWith(".folder.xml") && n.Contains("ROOT/"));
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithChildPage_FolderTitlePreservesOriginalName()
    {
        // DNN page names like "About Us" contain spaces.  The folder slug is
        // "about-us" (URL-safe), but the folder <title> must keep the original
        // name so that DotCMS $navtool.title shows it with spaces.
        var pages = new[]
        {
            new DnnPortalPage("aaa", "About Us", "About Us", "", "//About Us",  0, true, ""),
            new DnnPortalPage("bbb", "Our Story", "Our Story", "", "//About Us//Our Story", 1, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("About Us", "<p>About</p>", TabUniqueId: "aaa"),
            new DnnHtmlContent("Our Story", "<p>Story</p>", TabUniqueId: "bbb"),
        };

        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents,
                themesZipPath: zipPath);

            string folderEntry = names.First(n => n.EndsWith(".folder.xml") && n.Contains("ROOT/"));
            string xml = ReadTarEntry(ms, folderEntry)!;

            // Folder name (path segment) should be the slug.
            Assert.Contains("<name>about-us</name>", xml);
            // Folder title should preserve the original DNN page name with spaces.
            Assert.Contains("<title>About Us</title>", xml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithChildPage_PageXmlHasCorrectParentPath()
    {
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Personal", "Personal", "", "//Personal",  0, true, ""),
            new DnnPortalPage("bbb", "Checking", "Checking", "", "//Personal//Checking", 1, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Personal", "<p>Personal</p>", TabUniqueId: "aaa"),
            new DnnHtmlContent("Checking", "<p>Checking</p>", TabUniqueId: "bbb"),
        };

        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents,
                themesZipPath: zipPath);

            // Find the child page XML.
            string? childPageXml = null;
            foreach (string name in names.Where(n =>
                n.Contains("/1/") && n.EndsWith(".content.xml") && !n.Contains("host.xml")))
            {
                string candidate = ReadTarEntry(ms, name)!;
                if (candidate.Contains("<assetSubType>htmlpageasset</assetSubType>")
                    && candidate.Contains("<string>checking</string>"))
                {
                    childPageXml = candidate;
                    break;
                }
            }

            Assert.NotNull(childPageXml);
            // The child page should have parentPath="/personal/".
            Assert.Contains("<parentPath>/personal/</parentPath>", childPageXml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_AdminChildPages_AreExcluded()
    {
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Home",  "Home",  "", "//Home",  0, true, ""),
            new DnnPortalPage("bbb", "Admin", "Admin", "", "//Admin", 0, false, ""),
            new DnnPortalPage("ccc", "Users", "Users", "", "//Admin//Users", 1, false, ""),
        };

        var (_, names) = WriteBundleWithPages(pages);

        // Only the Home page should produce a content.xml entry — Admin and
        // its children should be excluded.
        int pageCount = names.Count(n => n.Contains("/1/") && n.EndsWith(".content.xml")
            && !n.Contains("host.xml") && !n.Contains("htmlContent"));
        Assert.Equal(1, pageCount);
    }

    [Fact]
    public void Write_WithChildPage_ChildPageHasShowOnMenuTrue()
    {
        // Child pages that are visible in DNN (IsVisible=true) must be
        // marked showOnMenu=true in DotCMS so the $navtool includes them
        // in the navigation dropdown.
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Personal", "Personal", "", "//Personal",  0, true, ""),
            new DnnPortalPage("bbb", "Checking", "Checking", "", "//Personal//Checking", 1, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Personal", "<p>Personal</p>", TabUniqueId: "aaa"),
            new DnnHtmlContent("Checking", "<p>Checking</p>", TabUniqueId: "bbb"),
        };

        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents,
                themesZipPath: zipPath);

            // Find the child page XML (the "checking" page, not the "index" page).
            string? childPageXml = null;
            foreach (string name in names.Where(n =>
                n.Contains("/1/") && n.EndsWith(".content.xml") && !n.Contains("host.xml")))
            {
                string candidate = ReadTarEntry(ms, name)!;
                if (candidate.Contains("<assetSubType>htmlpageasset</assetSubType>")
                    && candidate.Contains("<string>checking</string>"))
                {
                    childPageXml = candidate;
                    break;
                }
            }

            Assert.NotNull(childPageXml);
            // The child page must have showOnMenu=true.
            Assert.Contains("<string>showOnMenu</string>", childPageXml);
            Assert.Contains("<boolean>true</boolean>", childPageXml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithChildPage_FolderHasShowOnMenuTrue()
    {
        // Navigation folders must have showOnMenu=true so the $navtool
        // includes the folder (and its children) in the top-level nav.
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Personal", "Personal", "", "//Personal",  0, true, ""),
            new DnnPortalPage("bbb", "Checking", "Checking", "", "//Personal//Checking", 1, true, ""),
        };

        var (ms, names) = WriteBundleWithPages(pages);

        string folderEntry = names.First(n => n.EndsWith(".folder.xml") && n.Contains("ROOT/"));
        string xml = ReadTarEntry(ms, folderEntry)!;

        Assert.Contains("<showOnMenu>true</showOnMenu>", xml);
    }

    [Fact]
    public void Write_WithChildPage_FolderSortOrderMatchesParentTabOrder()
    {
        // The navigation folder's sortOrder should match the Level-0
        // parent page's TabOrder so the folder appears in the correct
        // position in the DotCMS $navtool navigation.
        var pages = new[]
        {
            new DnnPortalPage("aaa", "Personal", "Personal", "", "//Personal",  0, true, "", 5),
            new DnnPortalPage("bbb", "Checking", "Checking", "", "//Personal//Checking", 1, true, "", 1),
        };

        var (ms, names) = WriteBundleWithPages(pages);

        string folderEntry = names.First(n => n.EndsWith(".folder.xml") && n.Contains("ROOT/"));
        string xml = ReadTarEntry(ms, folderEntry)!;

        // Folder sort order should be 5 (from the Level-0 "Personal" page),
        // not 0 (the default) or 1 (from the Level-1 "Checking" page).
        Assert.Contains("<sortOrder>5</sortOrder>", xml);
    }

    [Fact]
    public void BuildNavSnippet_ContainsChildrenFallback()
    {
        // The nav Velocity snippet should include a fallback that
        // explicitly loads folder children via $navtool.getNav() when
        // $navItem.children is not automatically populated.
        string snippet = BundleWriter.BuildNavSnippet(
            @"<dnn:MENU runat=""server"" />");

        Assert.Contains("$navItem.children", snippet);
        Assert.Contains("$navtool.getNav($navItem.href)", snippet);
        Assert.Contains("endsWith(\"/\")", snippet);
    }

    // ------------------------------------------------------------------
    // Per-pane container multiTree tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithPerPaneContainer_MultiTreeUsesCorrectContainerId()
    {
        // When content items use a non-default container (e.g. hpcard.ascx),
        // the multiTree entries must reference the matching container ID, not
        // the default container.
        string tabId = "per-pane-ctr-test";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "Home", "Home", "", "//Home", 0, true, ""),
        };

        // Build a themes zip with TWO containers: standard and hpcard.
        string containerAscx1 = """
            <%@ Control Inherits="DotNetNuke.UI.Containers.Container" %>
            <div id="ContentPane" runat="server"></div>
            """;
        string containerAscx2 = """
            <%@ Control Inherits="DotNetNuke.UI.Containers.Container" %>
            <div class="card"><div id="ContentPane" runat="server"></div></div>
            """;
        string skinAscx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div id="ContentPane" runat="server"></div>
            <div id="CTA1" runat="server"></div>
            """;

        string zipPath = BuildThemesZipWithMultipleContainers(
            skinAscx, containerAscx1, "standard", containerAscx2, "hpcard");
        try
        {
            // Content in CTA1 pane uses the hpcard container.
            var contents = new[]
            {
                new DnnHtmlContent("Welcome", "<h1>Hello</h1>",
                    TabUniqueId: tabId, PaneName: "ContentPane"),
                new DnnHtmlContent("Card",    "<p>Card content</p>",
                    TabUniqueId: tabId, PaneName: "CTA1",
                    ContainerSrc: "[L]Containers/TestTheme/hpcard.ascx"),
            };

            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents,
                themesZipPath: zipPath);

            // Find the page XML.
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

            // The multiTree should contain two entries with different container IDs.
            // Count distinct parent2 values (container IDs) in multiTree entries.
            var parent2Values = new List<string>();
            string search = "<string>parent2</string>";
            int idx = 0;
            while ((idx = pageXml!.IndexOf(search, idx, StringComparison.Ordinal)) >= 0)
            {
                int valStart = pageXml.IndexOf("<string>", idx + search.Length, StringComparison.Ordinal);
                int valEnd   = pageXml.IndexOf("</string>", valStart + 8, StringComparison.Ordinal);
                if (valStart >= 0 && valEnd > valStart)
                    parent2Values.Add(pageXml[(valStart + 8)..valEnd]);
                idx = valEnd > 0 ? valEnd : idx + 1;
            }

            Assert.Equal(2, parent2Values.Count);
            // The two multiTree entries should reference different containers.
            Assert.NotEqual(parent2Values[0], parent2Values[1]);
        }
        finally { File.Delete(zipPath); }
    }

    private static string BuildThemesZipWithMultipleContainers(
        string skinAscx, string containerAscx1, string containerName1,
        string containerAscx2, string containerName2)
    {
        string zipPath = Path.GetTempFileName();
        File.Delete(zipPath); // ZipFile.Open with Create requires the file to not exist.
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var skinEntry = zip.CreateEntry("_default/Skins/TestTheme/Home.ascx");
            using (var sw = new StreamWriter(skinEntry.Open()))
                sw.Write(skinAscx);

            var ctrEntry1 = zip.CreateEntry($"_default/Containers/TestTheme/{containerName1}.ascx");
            using (var sw = new StreamWriter(ctrEntry1.Open()))
                sw.Write(containerAscx1);

            var ctrEntry2 = zip.CreateEntry($"_default/Containers/TestTheme/{containerName2}.ascx");
            using (var sw = new StreamWriter(ctrEntry2.Open()))
                sw.Write(containerAscx2);
        }
        return zipPath;
    }

    // ------------------------------------------------------------------
    // Tests for duplicate folder / identifier deduplication
    // ------------------------------------------------------------------

    private static (MemoryStream stream, List<string> entryNames) WriteBundleWithPagesAndFiles(
        IReadOnlyList<DnnPortalPage> pages,
        IReadOnlyList<DnnPortalFile> files,
        IReadOnlyList<DnnHtmlContent>? htmlContents = null,
        string siteName = "Test Site",
        string? themesZipPath = null)
    {
        var ms = new MemoryStream();
        BundleWriter.Write([MakeHtmlContentType()], ms, themesZipPath, siteName,
            htmlContents, pages, files);
        ms.Position = 0;

        var names = new List<string>();
        using var gz  = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gz);

        TarEntry? entry;
        while ((entry = tar.GetNextEntry(copyData: true)) is not null)
            names.Add(entry.Name);

        ms.Position = 0;
        return (ms, names);
    }

    [Fact]
    public void Write_WithHomePageName_PageXmlUsesIndexUrl()
    {
        // Home pages should use "index" as the URL slug.
        var pages = new[]
        {
            new DnnPortalPage("home-1", "Home", "Home Page", "", "//Home", 0, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Welcome", "<h1>Hello</h1>", TabUniqueId: "home-1"),
        };

        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents, themesZipPath: zipPath);

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
            Assert.Contains("<assetName>index</assetName>", pageXml);
            Assert.Contains("<string>index</string>", pageXml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithNonHomePageName_PageXmlUsesSlug()
    {
        // Non-home pages should use their normal slugified URL.
        var pages = new[]
        {
            new DnnPortalPage("about-1", "About", "About Us", "", "//About", 0, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("About", "<p>About</p>", TabUniqueId: "about-1"),
        };

        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents, themesZipPath: zipPath);

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
            Assert.Contains("<assetName>about</assetName>", pageXml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithPageFolderAndFileFolder_ProducesOnlyOneFolderXml()
    {
        // When a page hierarchy creates folder "personal" and portal files
        // also use folder "personal/", only one folder XML entry should be
        // written to avoid duplicate identifier constraint violations.
        var pages = new[]
        {
            new DnnPortalPage("parent-1", "Personal", "Personal", "", "//Personal",  0, true, ""),
            new DnnPortalPage("child-1",  "Checking", "Checking", "", "//Personal//Checking", 1, true, ""),
        };
        var files = new[]
        {
            new DnnPortalFile(
                "ff000000-0000-0000-0000-000000000001",
                "ff000000-0000-0000-0000-000000000002",
                "doc.pdf", "personal/", "application/pdf",
                [0x25, 0x50, 0x44]),
        };

        var (ms, names) = WriteBundleWithPagesAndFiles(pages, files);

        // Count folder XML entries whose content names the "personal" folder.
        var folderEntries = names.Where(n => n.EndsWith(".folder.xml")).ToList();
        int personalFolderCount = 0;
        foreach (string entry in folderEntries)
        {
            string xml = ReadTarEntry(ms, entry)!;
            if (xml.Contains("<name>personal</name>") || xml.Contains("<name>Personal</name>"))
                personalFolderCount++;
        }

        Assert.Equal(1, personalFolderCount);
    }

    [Fact]
    public void Write_WithPageFolderAndFileFolder_FileAssetReferencesSharedFolderInode()
    {
        // When a page hierarchy creates folder "personal" and portal files
        // also use folder "personal/", the file asset should reference
        // the same folder inode as the page folder.
        var pages = new[]
        {
            new DnnPortalPage("parent-1", "Personal", "Personal", "", "//Personal",  0, true, ""),
            new DnnPortalPage("child-1",  "Checking", "Checking", "", "//Personal//Checking", 1, true, ""),
        };
        var files = new[]
        {
            new DnnPortalFile(
                "ff000000-0000-0000-0000-000000000001",
                "ff000000-0000-0000-0000-000000000002",
                "doc.pdf", "personal/", "application/pdf",
                [0x25, 0x50, 0x44]),
        };

        var (ms, names) = WriteBundleWithPagesAndFiles(pages, files);

        // Find the folder inode from the folder XML.
        string? folderInode = null;
        foreach (string entry in names.Where(n => n.EndsWith(".folder.xml")))
        {
            string xml = ReadTarEntry(ms, entry)!;
            if (xml.Contains("<name>personal</name>") || xml.Contains("<name>Personal</name>"))
            {
                // Extract inode: <inode>...</inode>
                int start = xml.IndexOf("<inode>") + "<inode>".Length;
                int end   = xml.IndexOf("</inode>", start);
                folderInode = xml[start..end];
                break;
            }
        }
        Assert.NotNull(folderInode);

        // Find the file asset XML and verify it uses the same folder inode.
        string? fileXml = null;
        foreach (string name in names.Where(n =>
            n.Contains("/1/") && n.EndsWith(".content.xml") && !n.Contains("host.xml")))
        {
            string candidate = ReadTarEntry(ms, name)!;
            if (candidate.Contains("<assetSubType>FileAsset</assetSubType>")
                && candidate.Contains("doc.pdf"))
            {
                fileXml = candidate;
                break;
            }
        }
        Assert.NotNull(fileXml);
        Assert.Contains($"<string>{folderInode}</string>", fileXml);
    }

    [Fact]
    public void Write_WithParentPageConflictingWithChildFolder_ParentMovedInsideFolder()
    {
        // When a top-level page "Services" has child pages, the page's URL
        // "services" would collide with the folder "services/" created for
        // children. The parent page should be moved inside the folder as
        // "index" to avoid the identifier constraint violation.
        var pages = new[]
        {
            new DnnPortalPage("svc-1",   "Services", "Services", "", "//Services",           0, true, ""),
            new DnnPortalPage("price-1", "Pricing",  "Pricing",  "", "//Services//Pricing",  1, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Services", "<p>Services</p>", TabUniqueId: "svc-1"),
            new DnnHtmlContent("Pricing",  "<p>Pricing</p>",  TabUniqueId: "price-1"),
        };

        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleWithPagesAndContents(pages, contents, themesZipPath: zipPath);

            // Find the "Services" parent page XML.
            string? servicesPageXml = null;
            foreach (string name in names.Where(n =>
                n.Contains("/1/") && n.EndsWith(".content.xml") && !n.Contains("host.xml")))
            {
                string candidate = ReadTarEntry(ms, name)!;
                if (candidate.Contains("<assetSubType>htmlpageasset</assetSubType>")
                    && candidate.Contains("<string>Services</string>"))
                {
                    servicesPageXml = candidate;
                    break;
                }
            }

            Assert.NotNull(servicesPageXml);
            // The parent page should be at parentPath="/services/" with
            // assetName "index" (not at "/" with assetName "services").
            Assert.Contains("<assetName>index</assetName>", servicesPageXml);
            Assert.Contains("<parentPath>/services/</parentPath>", servicesPageXml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithTopLevelPageAndFileFolderSameName_PageMovedInsideFolder()
    {
        // When a top-level page "images" would collide with a portal-file
        // folder "images/", the page should be moved inside the folder
        // as "index".
        var pages = new[]
        {
            new DnnPortalPage("img-1", "images", "Images", "", "//images", 0, true, ""),
        };
        var files = new[]
        {
            new DnnPortalFile(
                "ff000000-0000-0000-0000-000000000010",
                "ff000000-0000-0000-0000-000000000011",
                "photo.jpg", "images/", "image/jpeg",
                [0xFF, 0xD8]),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Gallery", "<p>Gallery</p>", TabUniqueId: "img-1"),
        };

        string zipPath = BuildThemesZip();
        try
        {
            var (ms, names) = WriteBundleWithPagesAndFiles(pages, files, contents, themesZipPath: zipPath);

            // Find the "images" page XML.
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
            Assert.Contains("<assetName>index</assetName>", pageXml);
            Assert.Contains("<parentPath>/images/</parentPath>", pageXml);
        }
        finally { File.Delete(zipPath); }
    }

    // ------------------------------------------------------------------
    // DefaultSkinSrc / ResolveTemplateId tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithDefaultSkinSrc_EmptySkinSrcPageUsesInnerTemplate()
    {
        // When a page has no SkinSrc but a DefaultPortalSkin is provided,
        // the page must use the Inner template, not the Home template.
        string tabId = "inner-page-test";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "Our Story", "Our Story", "", "//OurStory", 0, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("About Us", "<h1>About Us</h1>", TabUniqueId: tabId, PaneName: "ContentPane"),
        };

        string zipPath = BuildThemesZipWithTwoSkins();
        try
        {
            var ms = new MemoryStream();
            BundleWriter.Write([MakeHtmlContentType()], ms, zipPath, "Test Site",
                contents, pages, defaultSkinSrc: "[L]Skins/TestTheme/Inner.ascx");
            ms.Position = 0;

            // Read all template XMLs to find the Inner template's identifier.
            var templateIds = new Dictionary<string, string>();
            var names = new List<string>();
            using (var gz  = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true))
            using (var tar = new TarReader(gz))
            {
                TarEntry? entry;
                while ((entry = tar.GetNextEntry(copyData: true)) is not null)
                {
                    names.Add(entry.Name);
                    if (entry.Name.EndsWith(".template.template.xml") && entry.DataStream is not null)
                    {
                        using var sr = new StreamReader(entry.DataStream, leaveOpen: true);
                        string xml = sr.ReadToEnd();
                        // Extract the template name and identifier from the XML.
                        var nameMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<title>(.*?)</title>");
                        var idMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<identifier>(.*?)</identifier>");
                        if (nameMatch.Success && idMatch.Success)
                            templateIds[nameMatch.Groups[1].Value] = idMatch.Groups[1].Value;
                    }
                }
            }

            Assert.True(templateIds.ContainsKey("Inner"), "Inner template should exist in bundle");
            Assert.True(templateIds.ContainsKey("Home"), "Home template should exist in bundle");
            string innerTemplateId = templateIds["Inner"];

            // Read the page XML and verify it references the Inner template.
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
            Assert.Contains(innerTemplateId, pageXml);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Write_WithDefaultSkinSrc_ExplicitSkinSrcOverridesDefault()
    {
        // When a page has an explicit SkinSrc, it should use that template,
        // not the DefaultPortalSkin.
        string tabId = "explicit-skin-test";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "Home", "Home", "", "//Home", 0, true,
                "[L]Skins/TestTheme/Home.ascx"),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Welcome", "<h1>Hello</h1>", TabUniqueId: tabId, PaneName: "ContentPane"),
        };

        string zipPath = BuildThemesZipWithTwoSkins();
        try
        {
            var ms = new MemoryStream();
            BundleWriter.Write([MakeHtmlContentType()], ms, zipPath, "Test Site",
                contents, pages, defaultSkinSrc: "[L]Skins/TestTheme/Inner.ascx");
            ms.Position = 0;

            // Read template XMLs.
            var templateIds = new Dictionary<string, string>();
            var names = new List<string>();
            using (var gz  = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: true))
            using (var tar = new TarReader(gz))
            {
                TarEntry? entry;
                while ((entry = tar.GetNextEntry(copyData: true)) is not null)
                {
                    names.Add(entry.Name);
                    if (entry.Name.EndsWith(".template.template.xml") && entry.DataStream is not null)
                    {
                        using var sr = new StreamReader(entry.DataStream, leaveOpen: true);
                        string xml = sr.ReadToEnd();
                        var nameMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<title>(.*?)</title>");
                        var idMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<identifier>(.*?)</identifier>");
                        if (nameMatch.Success && idMatch.Success)
                            templateIds[nameMatch.Groups[1].Value] = idMatch.Groups[1].Value;
                    }
                }
            }

            Assert.True(templateIds.ContainsKey("Home"), "Home template should exist in bundle");
            string homeTemplateId = templateIds["Home"];

            // Read the page XML and verify it references the Home template.
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
            Assert.Contains(homeTemplateId, pageXml);
        }
        finally { File.Delete(zipPath); }
    }

    /// <summary>
    /// Builds a themes zip with two skins (Home.ascx and Inner.ascx) and a
    /// single container, for testing DefaultPortalSkin template resolution.
    /// </summary>
    private static string BuildThemesZipWithTwoSkins()
    {
        string containerAscx = """
            <%@ Control Inherits="DotNetNuke.UI.Containers.Container" %>
            <div id="ContentPane" runat="server"></div>
            """;
        string homeSkinAscx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div id="ContentPane" runat="server"></div>
            <div id="SideBar" runat="server"></div>
            """;
        string innerSkinAscx = """
            <%@ Control Inherits="DotNetNuke.UI.Skins.Skin" %>
            <div id="ContentPane" class="col-md-9" runat="server"></div>
            <div id="SidePane" class="col-md-3" runat="server"></div>
            """;

        string path = Path.GetTempFileName() + ".zip";
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry1 = zip.CreateEntry("_default/Containers/TestTheme/standard.ascx");
            using (var w = new StreamWriter(entry1.Open())) w.Write(containerAscx);

            var entry2 = zip.CreateEntry("_default/Skins/TestTheme/Home.ascx");
            using (var w = new StreamWriter(entry2.Open())) w.Write(homeSkinAscx);

            var entry3 = zip.CreateEntry("_default/Skins/TestTheme/Inner.ascx");
            using (var w = new StreamWriter(entry3.Open())) w.Write(innerSkinAscx);

            var entry4 = zip.CreateEntry("_default/Skins/TestTheme/skin.css");
            using (var w = new StreamWriter(entry4.Open())) w.Write("body{}");
        }
        return path;
    }

    // ------------------------------------------------------------------
    // ------------------------------------------------------------------
    // Slider + Slide widget tests
    // ------------------------------------------------------------------

    [Fact]
    public void Write_WithSliderSlides_WritesSlideAndSliderContentTypesAndContainer()
    {
        // When slider slides are provided, the bundle must contain:
        // 1. A Slide content type and a Slider content type
        // 2. A Slider container with Velocity rendering code
        // 3. Individual Slide contentlets
        // 4. A Slider (parent) contentlet referencing the slides
        var slideCt = new DotCmsContentType
        {
            Name     = "Slide",
            Variable = "slide",
            Fields   =
            [
                new DotCmsField { Name = "Title",       Variable = "title",       DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Description", Variable = "description", DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Image",       Variable = "image",       DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Link",        Variable = "link",        DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Link Text",   Variable = "linkText",    DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
            ]
        };
        var sliderCt = new DotCmsContentType
        {
            Name     = "Slider",
            Variable = "slider",
            Fields   =
            [
                new DotCmsField { Name = "Title",  Variable = "title",  DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Slides", Variable = "slides", DataType = "SYSTEM",
                    Clazz = "com.dotcms.contenttype.model.field.RelationshipField",
                    RelationType = "slide" },
            ]
        };

        var slides = new[]
        {
            new DnnSliderSlide("Banner 1", "Welcome text", "/images/slide1.jpg", "/about",
                TabUniqueId: "tab-1", PaneName: "ContentPane", SortOrder: 0),
            new DnnSliderSlide("Banner 2", "More info", "/images/slide2.jpg", "/services",
                TabUniqueId: "tab-1", PaneName: "ContentPane", SortOrder: 1),
        };

        using var ms = new MemoryStream();
        BundleWriter.Write([slideCt, sliderCt], ms, siteName: "test-site", sliderSlides: slides);
        ms.Position = 0;

        var entries = ReadAllTarEntries(ms);
        var names   = entries.Select(e => e.Name).ToList();

        // Both content types must be written.
        Assert.Contains(names, n => n.Contains("contentType.json"));

        // Slider container must be present.
        Assert.Contains(names, n => n.Contains(".containers.container.xml"));
        var containerEntry = entries.First(e => e.Name.Contains(".containers.container.xml"));
        string containerXml = Encoding.UTF8.GetString(containerEntry.Content);
        Assert.Contains("Slider", containerXml);
        Assert.Contains("dotContentMap.slides", containerXml);

        // At least 3 content.xml entries: 2 slides + 1 slider.
        var contentXmlEntries = entries.Where(e =>
            e.Name.Contains(".content.xml") && !e.Name.Contains("host")).ToList();
        Assert.True(contentXmlEntries.Count >= 3,
            $"Expected at least 3 content.xml entries (2 slides + 1 slider), got {contentXmlEntries.Count}");

        // Verify slide data is in the contentlet XML.
        bool foundSlide1 = contentXmlEntries.Any(e =>
            Encoding.UTF8.GetString(e.Content).Contains("Banner 1"));
        bool foundSlide2 = contentXmlEntries.Any(e =>
            Encoding.UTF8.GetString(e.Content).Contains("Banner 2"));
        Assert.True(foundSlide1, "Slide 'Banner 1' not found in content entries");
        Assert.True(foundSlide2, "Slide 'Banner 2' not found in content entries");

        // Verify the Slider (parent) contentlet exists and stores slide
        // identifiers in the "slides" map field.
        bool foundSliderParent = contentXmlEntries.Any(e =>
        {
            string xml = Encoding.UTF8.GetString(e.Content);
            return xml.Contains("<assetSubType>slider</assetSubType>") && xml.Contains("slides");
        });
        Assert.True(foundSliderParent, "Slider parent contentlet with slides relationship field not found");
    }

    [Fact]
    public void Write_WithSliderSlides_SlideContentletContainsAllFields()
    {
        var slideCt = new DotCmsContentType
        {
            Name     = "Slide",
            Variable = "slide",
            Fields   =
            [
                new DotCmsField { Name = "Title",       Variable = "title",       DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Description", Variable = "description", DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Image",       Variable = "image",       DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Link",        Variable = "link",        DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Link Text",   Variable = "linkText",    DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
            ]
        };
        var sliderCt = new DotCmsContentType
        {
            Name     = "Slider",
            Variable = "slider",
            Fields   =
            [
                new DotCmsField { Name = "Title",  Variable = "title",  DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Slides", Variable = "slides", DataType = "SYSTEM",
                    Clazz = "com.dotcms.contenttype.model.field.RelationshipField",
                    RelationType = "slide" },
            ]
        };

        var slides = new[]
        {
            new DnnSliderSlide("My Slide", "Description text", "/img/hero.jpg", "/contact",
                LinkText: "Contact Us",
                TabUniqueId: "t1", PaneName: "BannerPane", SortOrder: 0),
        };

        using var ms = new MemoryStream();
        BundleWriter.Write([slideCt, sliderCt], ms, siteName: "test-site", sliderSlides: slides);
        ms.Position = 0;

        var entries = ReadAllTarEntries(ms);
        var slideEntries = entries.Where(e =>
            e.Name.Contains(".content.xml") && !e.Name.Contains("host")).ToList();

        // Find the slide contentlet XML (contains the field data, not the parent slider).
        string? slideXml = slideEntries
            .Select(e => Encoding.UTF8.GetString(e.Content))
            .FirstOrDefault(xml => xml.Contains("My Slide") && xml.Contains("slide"));
        Assert.NotNull(slideXml);

        // All slide fields must be present in the XML.
        Assert.Contains("My Slide", slideXml);
        Assert.Contains("Description text", slideXml);
        Assert.Contains("/img/hero.jpg", slideXml);
        Assert.Contains("/contact", slideXml);
        Assert.Contains("Contact Us", slideXml);
        Assert.Contains("slide", slideXml);
    }

    [Fact]
    public void BuildSliderContainerVelocity_ContainsFisSliderStructure()
    {
        string velocity = BundleWriter.BuildSliderContainerVelocity();

        // The Velocity template must include FisSlider CSS classes and IDs.
        Assert.Contains("Mvc-FisSliderModule-Container", velocity);
        Assert.Contains("slideshow", velocity);
        Assert.Contains("slide-container", velocity);
        Assert.Contains("slide-item", velocity);
        Assert.Contains("slide-title", velocity);
        Assert.Contains("slide-desc", velocity);
        Assert.Contains("slide-arrows", velocity);
        Assert.Contains("slide-nav", velocity);
        Assert.Contains("slide-link", velocity);
        Assert.Contains("dot", velocity);

        // Must reference the slider content fields via $dotContentMap (per-contentlet context).
        Assert.Contains("dotContentMap.title", velocity);
        Assert.Contains("$dotContentMap.slides", velocity);
        Assert.Contains("slide.title", velocity);
        Assert.Contains("slide.description", velocity);
        Assert.Contains("slide.image", velocity);
        Assert.Contains("slide.link", velocity);
        Assert.Contains("slide.linkText", velocity);

        // External CSS and JS must be linked (no inline <style>/<script> for CSP compliance).
        // When no theme prefix is provided, paths fall back to the site root.
        Assert.Contains("<link rel=\"stylesheet\" href=\"/slider.css\"", velocity);
        Assert.Contains("<script src=\"/slider.js\"", velocity);
        Assert.DoesNotContain("<style>", velocity);
        // No inline script blocks — only <script src="…"> tags.
        Assert.DoesNotContain("<script>\n", velocity);
        Assert.DoesNotContain("<script>\r", velocity);

        // Text overlay must use slide-text-container class.
        Assert.Contains("slide-text-container", velocity);

        // data-slider-id attribute is used by external JS to initialise each instance.
        Assert.Contains("data-slider-id", velocity);
    }

    [Fact]
    public void BuildSliderContainerVelocity_WithThemePrefix_UsesThemePaths()
    {
        string velocity = BundleWriter.BuildSliderContainerVelocity("/application/themes/Xcillion");

        // CSS and JS must reference the theme directory, not the site root.
        Assert.Contains("<link rel=\"stylesheet\" href=\"/application/themes/Xcillion/slider.css\"", velocity);
        Assert.Contains("<script src=\"/application/themes/Xcillion/slider.js\"", velocity);
    }

    [Fact]
    public void BuildSliderCss_ContainsSmoothTransitionAndNavStyles()
    {
        string css = BundleWriter.BuildSliderCss();

        // Smooth fade transition (not display:none/block).
        Assert.Contains("transition: opacity", css);
        Assert.Contains("opacity: 0", css);
        Assert.Contains("opacity: 1", css);
        Assert.DoesNotContain("display: none", css);

        // No overlay background color.
        Assert.DoesNotContain("background-color", css);

        // Navigation arrows must be styled and visible.
        Assert.Contains(".slide-arrows", css);
        Assert.Contains("z-index", css);
        Assert.Contains(".slide-arrows button", css);

        // Navigation dots must be styled and visible.
        Assert.Contains(".slide-nav", css);
        Assert.Contains(".slide-nav .dot", css);
        Assert.Contains(".dot.active", css);
    }

    [Fact]
    public void BuildSliderCss_SelectorsAreScopedUnderSlideshow()
    {
        string css = BundleWriter.BuildSliderCss();

        // All selectors must be scoped under ".slideshow" to prevent
        // imported DNN skin CSS (e.g. #LoginSlideshow .slideshow .slide-item
        // { position: relative }) from overriding position: absolute and
        // making slides disappear.
        Assert.Contains(".slideshow .slide-item", css);
        Assert.Contains(".slideshow .slideshowContent", css);
        Assert.Contains(".slideshow .slide-text-container", css);
        Assert.Contains(".slideshow .slide-arrows", css);
        Assert.Contains(".slideshow .slide-nav", css);

        // position: absolute on .slide-item must be !important to beat
        // ID-level selectors from DNN skin CSS.
        Assert.Contains("position: absolute !important", css);
    }

    [Fact]
    public void BuildNavSnippet_ResetsChildrenEachIteration()
    {
        // The Velocity snippet must reset $children to an empty list []
        // at the start of each #foreach iteration to work around the
        // Velocity quirk where #set does not clear a variable when the
        // RHS evaluates to null.  Without this, a previous iteration's
        // children could leak into the current one.
        string snippet = BundleWriter.BuildNavSnippet(
            @"<dnn:MENU runat=""server"" />");

        // The reset must appear inside the loop before reading $navItem.children.
        int foreachIdx = snippet.IndexOf("#foreach($navItem in $navItems)");
        int resetIdx   = snippet.IndexOf("#set($children = [])", foreachIdx);
        int readIdx    = snippet.IndexOf("#set($children = $navItem.children)", foreachIdx);

        Assert.True(foreachIdx > 0, "#foreach not found");
        Assert.True(resetIdx > foreachIdx, "#set($children = []) reset not found after #foreach");
        Assert.True(readIdx > resetIdx, "$navItem.children read should come after the reset");
    }

    [Fact]
    public void BuildNavSnippet_FallsBackToParentFolderPath()
    {
        // When a nav item's href does not end with "/" (e.g. an index page
        // inside a folder), the snippet should extract the parent folder
        // path and load children from there.
        string snippet = BundleWriter.BuildNavSnippet(
            @"<dnn:MENU runat=""server"" />");

        // Must contain the folder-path extraction using lastIndexOf/substring.
        Assert.Contains("lastIndexOf(\"/\")", snippet);
        Assert.Contains("substring(0,", snippet);
        // The extracted folder path is passed to $navtool.getNav with a trailing slash.
        Assert.Contains("$navtool.getNav(\"$folderPath/\")", snippet);
    }

    [Fact]
    public void BuildSliderCss_TextContainerClearsArrow()
    {
        // The slide text left padding must be large enough to clear the
        // 44px + 10px arrow at all viewport widths.  The desktop rule uses
        // max(15%, 70px) and the mobile rule uses a fixed 70px minimum.
        string css = BundleWriter.BuildSliderCss();

        Assert.Contains("max(15%, 70px)", css);
        Assert.Contains("padding: 7% 3% 7% 70px", css);
    }

    [Fact]
    public void BuildSliderJs_SelfInitialisesViaDataAttribute()
    {
        string js = BundleWriter.BuildSliderJs();

        // Must self-discover sliders via data-slider-id attribute.
        Assert.Contains("data-slider-id", js);
        Assert.Contains("data-slider-init", js);

        // Core slide logic must be present.
        Assert.Contains("showSlide", js);
        Assert.Contains("nextSlide", js);
        Assert.Contains("prevSlide", js);
        Assert.Contains("startAutoPlay", js);
        Assert.Contains("addEventListener", js);

        // Must use class-based queries, not ID-based lookups.
        Assert.Contains("querySelectorAll", js);
        Assert.DoesNotContain("getElementById", js);
    }

    [Theory]
    [InlineData("url('/Portals/MyPortal/images/bg.jpg')", "url('/images/bg.jpg')")]
    [InlineData("url(\"/Portals/FidelityBankTexas/images/Gray.jpg\")", "url(\"/images/Gray.jpg\")")]
    [InlineData("url(/Portals/VastBank/images/purplebg.jpg)", "url(/images/purplebg.jpg)")]
    [InlineData("url('/Portals/Acme/Css/font.woff')", "url('/Css/font.woff')")]
    [InlineData("color: red; /* no url */", "color: red; /* no url */")]
    public void RewriteCssPortalUrls_RewritesDnnPortalPaths(string input, string expected)
    {
        string result = BundleWriter.RewriteCssPortalUrls(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildRelationshipXml_ContainsExpectedStructure()
    {
        string xml = BundleWriter.BuildRelationshipXml(
            "rel-inode-123",
            "slider-ct-id",
            "slide-ct-id",
            "slider",
            "slides",
            "slider",
            cardinality: 1);

        Assert.Contains("com.dotcms.publisher.pusher.wrapper.RelationshipWrapper", xml);
        Assert.Contains("<inode>rel-inode-123</inode>", xml);
        Assert.Contains("<parentStructureInode>slider-ct-id</parentStructureInode>", xml);
        Assert.Contains("<childStructureInode>slide-ct-id</childStructureInode>", xml);
        Assert.Contains("<parentRelationName>slider</parentRelationName>", xml);
        Assert.Contains("<childRelationName>slides</childRelationName>", xml);
        Assert.Contains("<relationTypeValue>slider.slides</relationTypeValue>", xml);
        Assert.Contains("<cardinality>1</cardinality>", xml);
        Assert.Contains("<operation>PUBLISH</operation>", xml);
        Assert.Contains("<type>relationship</type>", xml);
    }

    [Fact]
    public void Write_WithSliderSlides_SliderContentletHasRelationshipField()
    {
        // Verify the parent Slider contentlet conveys relationship data via
        // <tree> entries (Map<String,Object> with parent/child/relation_type/
        // tree_order) rather than putting identifiers in the content map.
        // DotCMS ContentHandler.regenerateTree() processes these entries and
        // calls TreeFactory.saveTree() to create the relationship records.
        var slideCt = new DotCmsContentType
        {
            Name     = "Slide",
            Variable = "slide",
            Fields   =
            [
                new DotCmsField { Name = "Title", Variable = "title", DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Image", Variable = "image", DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
            ]
        };
        var sliderCt = new DotCmsContentType
        {
            Name     = "Slider",
            Variable = "slider",
            Fields   =
            [
                new DotCmsField { Name = "Title",  Variable = "title",  DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Slides", Variable = "slides", DataType = "SYSTEM",
                    Clazz = "com.dotcms.contenttype.model.field.RelationshipField",
                    RelationType = "slide" },
            ]
        };

        var slides = new[]
        {
            new DnnSliderSlide("Slide A", "", "/img/a.jpg", "#",
                TabUniqueId: "tab-1", PaneName: "BannerPane", SortOrder: 0),
            new DnnSliderSlide("Slide B", "", "/img/b.jpg", "#",
                TabUniqueId: "tab-1", PaneName: "BannerPane", SortOrder: 1),
        };

        using var ms = new MemoryStream();
        BundleWriter.Write([slideCt, sliderCt], ms, siteName: "test-site", sliderSlides: slides);
        ms.Position = 0;

        var entries = ReadAllTarEntries(ms);
        var contentXmlEntries = entries.Where(e =>
            e.Name.Contains(".content.xml") && !e.Name.Contains("host")).ToList();

        // Find the Slider parent contentlet (the one with assetSubType "slider").
        string? sliderXml = contentXmlEntries
            .Select(e => Encoding.UTF8.GetString(e.Content))
            .FirstOrDefault(xml => xml.Contains("<assetSubType>slider</assetSubType>"));
        Assert.NotNull(sliderXml);

        // Must NOT contain Tree objects (they cause ClassCastException on import).
        Assert.DoesNotContain("com.dotmarketing.beans.Tree", sliderXml);

        // Must NOT contain the "slides" field in the content map — the
        // Contentlet(Map) constructor copies via putAll(), and checkin()
        // then tries to cast each element to Contentlet (fails for strings).
        Assert.DoesNotMatch(@"<string>slides</string>", sliderXml);

        // Must NOT use a <list> of <string> in the content map — DotCMS casts
        // the list elements to Contentlet, not String.
        Assert.DoesNotContain("<list>", sliderXml);

        // The <tree> element must contain Map entries with parent/child/relation_type/tree_order.
        Assert.Contains("<tree>", sliderXml);
        Assert.Contains("relation_type", sliderXml);
        Assert.Contains("slider.slides", sliderXml);

        // Extract the Slide contentlet identifiers from their content.xml entries.
        var slideXmls = contentXmlEntries
            .Select(e => Encoding.UTF8.GetString(e.Content))
            .Where(xml => xml.Contains("<assetSubType>slide</assetSubType>"))
            .ToList();
        Assert.Equal(2, slideXmls.Count);

        // Each slide identifier should appear as a "child" in the Slider's <tree>.
        foreach (string slideXml in slideXmls)
        {
            // Extract the identifier from the slide XML.
            var match = System.Text.RegularExpressions.Regex.Match(slideXml,
                @"<id>\s*<id>([^<]+)</id>");
            Assert.True(match.Success, "Could not extract slide identifier");
            string slideId = match.Groups[1].Value;
            Assert.Contains(slideId, sliderXml);
        }
    }

    [Fact]
    public void Write_WithSliderSlides_SlideContentletsContainTreeEntries()
    {
        // DotCMS ContentHandler.cleanTrees() deletes ALL tree entries for every
        // contentlet during push-publish import (both parent and child sides),
        // then regenerateTree() recreates them from the contentlet's <tree>
        // element.  Without a tree entry on the child slide, importing the
        // slide AFTER the slider would remove the relationship record that
        // the slider's tree created, leaving $dotContentMap.slides empty.
        var slideCt = new DotCmsContentType
        {
            Name     = "Slide",
            Variable = "slide",
            Fields   =
            [
                new DotCmsField { Name = "Title", Variable = "title", DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Image", Variable = "image", DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
            ]
        };
        var sliderCt = new DotCmsContentType
        {
            Name     = "Slider",
            Variable = "slider",
            Fields   =
            [
                new DotCmsField { Name = "Title",  Variable = "title",  DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Slides", Variable = "slides", DataType = "SYSTEM",
                    Clazz = "com.dotcms.contenttype.model.field.RelationshipField",
                    RelationType = "slide" },
            ]
        };

        var slides = new[]
        {
            new DnnSliderSlide("Slide A", "", "/img/a.jpg", "#",
                TabUniqueId: "tab-1", PaneName: "BannerPane", SortOrder: 0),
            new DnnSliderSlide("Slide B", "", "/img/b.jpg", "#",
                TabUniqueId: "tab-1", PaneName: "BannerPane", SortOrder: 1),
        };

        using var ms = new MemoryStream();
        BundleWriter.Write([slideCt, sliderCt], ms, siteName: "test-site", sliderSlides: slides);
        ms.Position = 0;

        var entries = ReadAllTarEntries(ms);
        var contentXmlEntries = entries.Where(e =>
            e.Name.Contains(".content.xml") && !e.Name.Contains("host")).ToList();

        // Find the Slider parent identifier.
        string sliderXml = contentXmlEntries
            .Select(e => Encoding.UTF8.GetString(e.Content))
            .First(xml => xml.Contains("<assetSubType>slider</assetSubType>"));
        var sliderIdMatch = System.Text.RegularExpressions.Regex.Match(sliderXml,
            @"<id>\s*<id>([^<]+)</id>");
        Assert.True(sliderIdMatch.Success, "Could not extract slider identifier");
        string sliderId = sliderIdMatch.Groups[1].Value;

        // Each Slide contentlet must have a <tree> entry referencing its parent Slider.
        var slideXmls = contentXmlEntries
            .Select(e => Encoding.UTF8.GetString(e.Content))
            .Where(xml => xml.Contains("<assetSubType>slide</assetSubType>"))
            .ToList();
        Assert.Equal(2, slideXmls.Count);

        foreach (string slideXml in slideXmls)
        {
            // The tree must NOT be empty.
            Assert.DoesNotContain("<tree/>", slideXml);
            Assert.Contains("<tree>", slideXml);

            // Must contain the parent slider's identifier.
            Assert.Contains(sliderId, slideXml);

            // Must reference the relationship type.
            Assert.Contains("slider.slides", slideXml);

            // Must contain the relation_type key.
            Assert.Contains("relation_type", slideXml);
        }
    }

    [Fact]
    public void Write_WithSliderSlides_BundleContainsRelationshipXml()
    {
        // Verify the bundle contains a .relationship.xml file that defines
        // the Slider→Slide relationship.  DotCMS ContentTypeHandler sets
        // skipRelationshipCreation=true during push-publish import, so
        // the relationship must be explicitly included.
        var slideCt = new DotCmsContentType
        {
            Name     = "Slide",
            Variable = "slide",
            Fields   =
            [
                new DotCmsField { Name = "Title", Variable = "title", DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Image", Variable = "image", DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
            ]
        };
        var sliderCt = new DotCmsContentType
        {
            Name     = "Slider",
            Variable = "slider",
            Fields   =
            [
                new DotCmsField { Name = "Title",  Variable = "title",  DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Slides", Variable = "slides", DataType = "SYSTEM",
                    Clazz = "com.dotcms.contenttype.model.field.RelationshipField",
                    RelationType = "slide" },
            ]
        };

        var slides = new[]
        {
            new DnnSliderSlide("Slide A", "", "/img/a.jpg", "#",
                TabUniqueId: "tab-1", PaneName: "BannerPane", SortOrder: 0),
        };

        using var ms = new MemoryStream();
        BundleWriter.Write([slideCt, sliderCt], ms, siteName: "test-site", sliderSlides: slides);
        ms.Position = 0;

        var entries = ReadAllTarEntries(ms);

        // Find the .relationship.xml entry.
        var relEntries = entries.Where(e => e.Name.Contains(".relationship.xml")).ToList();
        Assert.Single(relEntries);

        string relXml = Encoding.UTF8.GetString(relEntries[0].Content);

        // Must be a RelationshipWrapper.
        Assert.Contains("com.dotcms.publisher.pusher.wrapper.RelationshipWrapper", relXml);

        // The relationTypeValue must follow new-style format: parentVariable.fieldVariable.
        Assert.Contains("<relationTypeValue>slider.slides</relationTypeValue>", relXml);

        // Must reference the Slider as parent and Slide as child.
        Assert.Contains("<childRelationName>slides</childRelationName>", relXml);

        // Cardinality 1 = ONE_TO_MANY.
        Assert.Contains("<cardinality>1</cardinality>", relXml);

        // Operation must be PUBLISH.
        Assert.Contains("<operation>PUBLISH</operation>", relXml);

        // Must contain parent and child content type IDs (non-empty).
        Assert.Contains("<parentStructureInode>", relXml);
        Assert.Contains("<childStructureInode>", relXml);
    }

    [Fact]
    public void Write_RelationshipFieldUsesSystemFieldDbColumn()
    {
        // A RelationshipField should get dbColumn "system_field", not "binaryN".
        var ct = new DotCmsContentType
        {
            Name     = "Slider",
            Variable = "slider",
            Fields   =
            [
                new DotCmsField { Name = "Title",  Variable = "title",  DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Slides", Variable = "slides", DataType = "SYSTEM",
                    Clazz = "com.dotcms.contenttype.model.field.RelationshipField",
                    RelationType = "slide" },
            ]
        };

        using var ms = new MemoryStream();
        BundleWriter.Write([ct], ms, siteName: "test-site");
        ms.Position = 0;

        var entries = ReadAllTarEntries(ms);
        var ctJsonEntry = entries.First(e => e.Name.Contains("contentType.json"));
        string ctJson = Encoding.UTF8.GetString(ctJsonEntry.Content);

        // The Slides field must use "system_field" as dbColumn.
        Assert.Contains("system_field", ctJson);
        // And should reference the relationType.
        Assert.Contains("slide", ctJson);
        // It must NOT use "binary1" for the relationship field.
        Assert.DoesNotContain("binary1", ctJson);
    }

    [Fact]
    public void Write_WithSliderSlides_TemplateContainsSliderParseContainer()
    {
        // When slider slides are placed on a page, the template must include
        // a #parseContainer for the slider container so DotCMS renders it.
        // The page's multiTree must reference the slider container with the
        // UUID matching the template's #parseContainer directive.
        var slideCt = new DotCmsContentType
        {
            Name     = "Slide",
            Variable = "slide",
            Fields   =
            [
                new DotCmsField { Name = "Title", Variable = "title", DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
            ]
        };
        var sliderCt = new DotCmsContentType
        {
            Name     = "Slider",
            Variable = "slider",
            Fields   =
            [
                new DotCmsField { Name = "Title",  Variable = "title",  DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Slides", Variable = "slides", DataType = "SYSTEM",
                    Clazz = "com.dotcms.contenttype.model.field.RelationshipField",
                    RelationType = "slide" },
            ]
        };

        string tabId = "tab-slider-parse";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "Home", "Home", "", "//Home", 0, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Welcome", "<h1>Hello</h1>", TabUniqueId: tabId, PaneName: "ContentPane"),
        };
        var slides = new[]
        {
            new DnnSliderSlide("Slide 1", "", "/img/1.jpg", "#",
                TabUniqueId: tabId, PaneName: "ContentPane", SortOrder: 0),
        };

        using var ms = new MemoryStream();
        BundleWriter.Write([MakeHtmlContentType(), slideCt, sliderCt], ms,
            siteName: "test-site", htmlContents: contents, pages: pages,
            sliderSlides: slides);
        ms.Position = 0;

        var entries = ReadAllTarEntries(ms);

        // The template XML must contain a #parseContainer for the slider container.
        var templateEntry = entries.FirstOrDefault(e => e.Name.Contains(".template.template.xml"));
        Assert.NotNull(templateEntry.Content);
        string templateXml = Encoding.UTF8.GetString(templateEntry.Content);

        // Count #parseContainer directives — should be at least 2:
        // one for the default HTML container and one for the slider container.
        int parseContainerCount = System.Text.RegularExpressions.Regex.Matches(
            templateXml, @"#parseContainer\(").Count;
        Assert.True(parseContainerCount >= 2,
            $"Expected at least 2 #parseContainer directives in template, got {parseContainerCount}");

        // The page multiTree must reference the slider container with
        // the same UUID used in the template's #parseContainer.
        // Find the Slider container ID from the container XML entry.
        var sliderContainerEntry = entries.First(e =>
            e.Name.Contains(".containers.container.xml")
            && Encoding.UTF8.GetString(e.Content).Contains("Slider"));
        string sliderContainerXml = Encoding.UTF8.GetString(sliderContainerEntry.Content);
        var containerIdMatch = System.Text.RegularExpressions.Regex.Match(
            sliderContainerXml, @"<containerId>\s*<id>([^<]+)</id>");
        Assert.True(containerIdMatch.Success, "Could not extract slider container ID from container XML");
        string sliderContainerId = containerIdMatch.Groups[1].Value;

        // The template must have #parseContainer(sliderContainerId, uuid).
        Assert.Contains(sliderContainerId, templateXml);

        // Extract the UUID used for the slider container in the template.
        // Note: the template body is XML-escaped, so apostrophes become &apos;
        string escapedSliderId = System.Text.RegularExpressions.Regex.Escape(
            System.Security.SecurityElement.Escape(sliderContainerId)!);
        var uuidMatch = System.Text.RegularExpressions.Regex.Match(
            templateXml, $@"#parseContainer\(&apos;{escapedSliderId}&apos;,\s*&apos;(\d+)&apos;\)");
        Assert.True(uuidMatch.Success,
            $"Template must have #parseContainer for slider container '{sliderContainerId}'");
        string sliderUuid = uuidMatch.Groups[1].Value;

        // The page's multiTree must reference the slider container
        // with this UUID as the relation_type.
        string pageEntryName = entries
            .Select(e => e.Name)
            .First(n => n.Contains("/1/") && n.EndsWith(".content.xml")
                && !n.Contains("host.xml")
                && Encoding.UTF8.GetString(
                    entries.First(e2 => e2.Name == n).Content)
                    .Contains("htmlpageasset"));
        string pageXml = Encoding.UTF8.GetString(
            entries.First(e => e.Name == pageEntryName).Content);

        // Page multiTree must contain the slider container ID.
        Assert.Contains(sliderContainerId, pageXml);

        // Page multiTree must use the slider UUID as relation_type for
        // the slider container entries.
        Assert.Contains(sliderUuid, pageXml);
    }

    [Fact]
    public void Write_WithSliderSlides_TemplateBodyContainsJqueryCdnLinks()
    {
        // Templates that contain slider panes must include jQuery, jQuery
        // Migrate, and jQuery UI CDN script tags in the body so the
        // slider JavaScript works (DNN ships jQuery by default but the
        // exported bundle doesn't include it).
        var slideCt = new DotCmsContentType
        {
            Name     = "Slide",
            Variable = "slide",
            Fields   =
            [
                new DotCmsField { Name = "Title", Variable = "title", DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
            ]
        };
        var sliderCt = new DotCmsContentType
        {
            Name     = "Slider",
            Variable = "slider",
            Fields   =
            [
                new DotCmsField { Name = "Title",  Variable = "title",  DataType = "TEXT",
                    Clazz = "com.dotcms.contenttype.model.field.TextField" },
                new DotCmsField { Name = "Slides", Variable = "slides", DataType = "SYSTEM",
                    Clazz = "com.dotcms.contenttype.model.field.RelationshipField",
                    RelationType = "slide" },
            ]
        };

        string tabId = "tab-jquery-cdn";
        var pages = new[]
        {
            new DnnPortalPage(tabId, "Home", "Home", "", "//Home", 0, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("Welcome", "<p>Hello</p>", TabUniqueId: tabId, PaneName: "ContentPane"),
        };
        var slides = new[]
        {
            new DnnSliderSlide("Slide A", "", "/img/a.jpg", "#",
                TabUniqueId: tabId, PaneName: "ContentPane", SortOrder: 0),
        };

        using var ms = new MemoryStream();
        BundleWriter.Write([MakeHtmlContentType(), slideCt, sliderCt], ms,
            siteName: "test-site", htmlContents: contents, pages: pages,
            sliderSlides: slides);
        ms.Position = 0;

        var entries = ReadAllTarEntries(ms);
        var templateEntry = entries.FirstOrDefault(e => e.Name.Contains(".template.template.xml"));
        Assert.NotNull(templateEntry.Content);
        string templateXml = Encoding.UTF8.GetString(templateEntry.Content);

        // The <body> element must contain CDN links for jQuery 3.5.1,
        // jQuery Migrate 3.4.0, and jQuery UI 1.12.2.
        Assert.Contains("jquery-3.5.1.min.js", templateXml);
        Assert.Contains("jquery-migrate-3.4.0.min.js", templateXml);
        Assert.Contains("jquery-ui.min.js", templateXml);
        Assert.Contains("code.jquery.com", templateXml);
    }

    [Fact]
    public void Write_WithoutSliderSlides_TemplateBodyStillContainsJquery()
    {
        // All templates must include jQuery CDN links in the body because
        // DNN ships jQuery by default and migrated pages may depend on it.
        var pages = new[]
        {
            new DnnPortalPage("tab1", "About", "About", "", "//About", 0, true, ""),
        };
        var contents = new[]
        {
            new DnnHtmlContent("About us", "<p>Info</p>", TabUniqueId: "tab1", PaneName: "ContentPane"),
        };

        using var ms = new MemoryStream();
        BundleWriter.Write([MakeHtmlContentType()], ms,
            siteName: "test-site", htmlContents: contents, pages: pages);
        ms.Position = 0;

        var entries = ReadAllTarEntries(ms);
        var templateEntry = entries.FirstOrDefault(e => e.Name.Contains(".template.template.xml"));
        Assert.NotNull(templateEntry.Content);
        string templateXml = Encoding.UTF8.GetString(templateEntry.Content);

        Assert.Contains("jquery-3.5.1.min.js", templateXml);
        Assert.Contains("jquery-migrate-3.4.0.min.js", templateXml);
    }

    private static List<(string Name, byte[] Content)> ReadAllTarEntries(Stream gzStream)
    {
        gzStream.Position = 0;
        using var gz = new GZipStream(gzStream, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gz);
        var result = new List<(string Name, byte[] Content)>();
        while (tar.GetNextEntry() is TarEntry entry)
        {
            using var ms2 = new MemoryStream();
            entry.DataStream?.CopyTo(ms2);
            result.Add((entry.Name, ms2.ToArray()));
        }
        return result;
    }

}
