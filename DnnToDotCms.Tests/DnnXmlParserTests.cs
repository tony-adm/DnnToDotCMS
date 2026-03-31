using System.IO.Compression;
using DnnToDotCms.Mappings;
using DnnToDotCms.Models;
using DnnToDotCms.Parser;

namespace DnnToDotCms.Tests;

public class DnnXmlParserTests
{
    // ------------------------------------------------------------------
    // Package manifest tests
    // ------------------------------------------------------------------

    [Fact]
    public void ParseXml_PackageManifest_ReturnsSingleModule()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <dotnetnuke type="Package" version="5.0">
              <packages>
                <package name="DNN_HTML" type="Module" version="9.3.2">
                  <friendlyName>HTML</friendlyName>
                  <description>Rich HTML content module</description>
                  <components>
                    <component type="Module">
                      <desktopModule>
                        <moduleName>DNN_HTML</moduleName>
                        <foldername>HTML</foldername>
                        <businessControllerClass>DotNetNuke.Modules.Html.HtmlTextController</businessControllerClass>
                        <moduleDefinitions>
                          <moduleDefinition>
                            <friendlyName>HTML</friendlyName>
                            <defaultCacheTime>0</defaultCacheTime>
                            <moduleControls>
                              <moduleControl>
                                <controlKey/>
                                <controlSrc>DesktopModules/HTML/HtmlModule.ascx</controlSrc>
                                <controlType>View</controlType>
                                <helpUrl/>
                              </moduleControl>
                            </moduleControls>
                          </moduleDefinition>
                        </moduleDefinitions>
                      </desktopModule>
                    </component>
                  </components>
                </package>
              </packages>
            </dotnetnuke>
            """;

        IReadOnlyList<DnnModule> modules = DnnXmlParser.ParseXml(xml);

        Assert.Single(modules);
        DnnModule m = modules[0];
        Assert.Equal("DNN_HTML", m.ModuleName);
        Assert.Equal("HTML", m.FriendlyName);
        Assert.Equal("Rich HTML content module", m.Description);
        Assert.Equal("HTML", m.FolderName);
        Assert.Equal("DotNetNuke.Modules.Html.HtmlTextController", m.BusinessControllerClass);
        Assert.Equal("9.3.2", m.Version);
    }

    [Fact]
    public void ParseXml_PackageManifest_ParsesModuleDefinitionsAndControls()
    {
        const string xml = """
            <dotnetnuke type="Package" version="5.0">
              <packages>
                <package name="TestMod" type="Module" version="1.0.0">
                  <components>
                    <component type="Module">
                      <desktopModule>
                        <moduleName>TestMod</moduleName>
                        <moduleDefinitions>
                          <moduleDefinition>
                            <friendlyName>Default</friendlyName>
                            <defaultCacheTime>30</defaultCacheTime>
                            <moduleControls>
                              <moduleControl>
                                <controlKey/>
                                <controlSrc>View.ascx</controlSrc>
                                <controlType>View</controlType>
                                <helpUrl/>
                              </moduleControl>
                              <moduleControl>
                                <controlKey>Edit</controlKey>
                                <controlSrc>Edit.ascx</controlSrc>
                                <controlType>Edit</controlType>
                                <helpUrl>help.htm</helpUrl>
                              </moduleControl>
                            </moduleControls>
                          </moduleDefinition>
                        </moduleDefinitions>
                      </desktopModule>
                    </component>
                  </components>
                </package>
              </packages>
            </dotnetnuke>
            """;

        IReadOnlyList<DnnModule> modules = DnnXmlParser.ParseXml(xml);

        Assert.Single(modules);
        Assert.Single(modules[0].Definitions);

        DnnModuleDefinition def = modules[0].Definitions[0];
        Assert.Equal("Default", def.FriendlyName);
        Assert.Equal(30, def.DefaultCacheTime);
        Assert.Equal(2, def.Controls.Count);

        DnnModuleControl viewCtrl = def.Controls[0];
        Assert.Equal(string.Empty, viewCtrl.ControlKey);
        Assert.Equal("View.ascx", viewCtrl.ControlSrc);
        Assert.Equal("View", viewCtrl.ControlType);

        DnnModuleControl editCtrl = def.Controls[1];
        Assert.Equal("Edit", editCtrl.ControlKey);
        Assert.Equal("help.htm", editCtrl.HelpUrl);
    }

    [Fact]
    public void ParseXml_PackageManifest_SkipsNonModulePackages()
    {
        const string xml = """
            <dotnetnuke type="Package" version="5.0">
              <packages>
                <package name="MySkin" type="Skin" version="1.0.0">
                  <friendlyName>A Skin</friendlyName>
                </package>
                <package name="MyModule" type="Module" version="2.0.0">
                  <friendlyName>A Module</friendlyName>
                  <components>
                    <component type="Module">
                      <desktopModule>
                        <moduleName>MyModule</moduleName>
                      </desktopModule>
                    </component>
                  </components>
                </package>
              </packages>
            </dotnetnuke>
            """;

        IReadOnlyList<DnnModule> modules = DnnXmlParser.ParseXml(xml);

        Assert.Single(modules);
        Assert.Equal("MyModule", modules[0].ModuleName);
    }

    [Fact]
    public void ParseXml_PackageManifest_MultipleModules()
    {
        const string xml = """
            <dotnetnuke type="Package" version="5.0">
              <packages>
                <package name="Mod1" type="Module" version="1.0.0">
                  <friendlyName>Module One</friendlyName>
                  <components>
                    <component type="Module">
                      <desktopModule><moduleName>Mod1</moduleName></desktopModule>
                    </component>
                  </components>
                </package>
                <package name="Mod2" type="Module" version="1.0.0">
                  <friendlyName>Module Two</friendlyName>
                  <components>
                    <component type="Module">
                      <desktopModule><moduleName>Mod2</moduleName></desktopModule>
                    </component>
                  </components>
                </package>
              </packages>
            </dotnetnuke>
            """;

        IReadOnlyList<DnnModule> modules = DnnXmlParser.ParseXml(xml);

        Assert.Equal(2, modules.Count);
        Assert.Equal("Mod1", modules[0].ModuleName);
        Assert.Equal("Mod2", modules[1].ModuleName);
    }

    // ------------------------------------------------------------------
    // IPortable module-content export tests
    // ------------------------------------------------------------------

    [Fact]
    public void ParseXml_IPortableExport_ParsesModuleTypeAndTitle()
    {
        const string xml = """
            <module type="HTML" version="1.0.0">
              <moduleTitle>Welcome Banner</moduleTitle>
              <moduleContent><![CDATA[<h1>Hello</h1>]]></moduleContent>
            </module>
            """;

        IReadOnlyList<DnnModule> modules = DnnXmlParser.ParseXml(xml);

        Assert.Single(modules);
        DnnModule m = modules[0];
        Assert.Equal("HTML", m.ModuleName);
        Assert.Equal("Welcome Banner", m.FriendlyName);
        Assert.Equal("1.0.0", m.Version);
        Assert.Equal("<h1>Hello</h1>", m.Content);
    }

    [Fact]
    public void ParseXml_IPortableExport_CapturesExtraElements()
    {
        const string xml = """
            <module type="Events" version="2.0.0">
              <moduleTitle>Upcoming Events</moduleTitle>
              <moduleContent/>
              <timezone>UTC-5</timezone>
              <maxItems>10</maxItems>
            </module>
            """;

        IReadOnlyList<DnnModule> modules = DnnXmlParser.ParseXml(xml);

        Assert.Single(modules);
        DnnModule m = modules[0];
        Assert.True(m.Extra.ContainsKey("timezone"));
        Assert.Equal("UTC-5", m.Extra["timezone"]);
        Assert.Equal("10", m.Extra["maxItems"]);
    }

    // ------------------------------------------------------------------
    // ParseExportFolder tests
    // ------------------------------------------------------------------

    /// <summary>
    /// Helper: build an in-memory folder structure matching the DNN official
    /// site-export layout and return the folder path.
    ///
    /// Layout produced:
    ///   &lt;tempDir&gt;/
    ///     export_packages.zip
    ///       └─ Module_TestMod_1.0.0.resources  (zip-in-zip)
    ///            └─ testmod.dnn                 (DNN package manifest XML)
    /// </summary>
    private static string BuildExportFolder(string dnnXml, string moduleName = "TestMod")
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        // inner zip: the .resources file
        using var innerStream = new MemoryStream();
        using (var innerZip = new ZipArchive(innerStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry dnnEntry = innerZip.CreateEntry($"{moduleName.ToLower()}.dnn");
            using StreamWriter w = new(dnnEntry.Open());
            w.Write(dnnXml);
        }

        // outer zip: export_packages.zip
        string outerPath = Path.Combine(dir, "export_packages.zip");
        using var outerZip = ZipFile.Open(outerPath, ZipArchiveMode.Create);
        ZipArchiveEntry resourceEntry =
            outerZip.CreateEntry($"Module_{moduleName}_1.0.0.resources");
        using (Stream dest = resourceEntry.Open())
        {
            innerStream.Position = 0;
            innerStream.CopyTo(dest);
        }

        return dir;
    }

    [Fact]
    public void ParseExportFolder_SingleModule_ReturnsModule()
    {
        const string xml = """
            <dotnetnuke type="Package" version="5.0">
              <packages>
                <package name="DNN_HTML" type="Module" version="9.11.2">
                  <friendlyName>HTML</friendlyName>
                  <description>HTML module</description>
                  <components>
                    <component type="Module">
                      <desktopModule>
                        <moduleName>DNN_HTML</moduleName>
                        <foldername>HTML</foldername>
                      </desktopModule>
                    </component>
                  </components>
                </package>
              </packages>
            </dotnetnuke>
            """;

        string folder = BuildExportFolder(xml, "DNN_HTML");
        try
        {
            IReadOnlyList<DnnModule> modules = DnnXmlParser.ParseExportFolder(folder);

            Assert.Single(modules);
            Assert.Equal("DNN_HTML", modules[0].ModuleName);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ParseExportFolder_MissingPackagesZip_ThrowsFileNotFoundException()
    {
        string emptyFolder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(emptyFolder);
        try
        {
            Assert.Throws<FileNotFoundException>(
                () => DnnXmlParser.ParseExportFolder(emptyFolder));
        }
        finally
        {
            Directory.Delete(emptyFolder, recursive: true);
        }
    }

    [Fact]
    public void ParseExportJson_ExportJsonFile_ReturnsModules()
    {
        // Arrange: build a folder with export_packages.zip and an export.json sidecar
        const string xml = """
            <dotnetnuke type="Package" version="5.0">
              <packages>
                <package name="DNN_HTML" type="Module" version="9.11.2">
                  <friendlyName>HTML</friendlyName>
                  <components>
                    <component type="Module">
                      <desktopModule>
                        <moduleName>DNN_HTML</moduleName>
                        <foldername>HTML</foldername>
                      </desktopModule>
                    </component>
                  </components>
                </package>
              </packages>
            </dotnetnuke>
            """;

        string folder = BuildExportFolder(xml, "DNN_HTML");
        string jsonFile = Path.Combine(folder, "export.json");
        File.WriteAllText(jsonFile, """{"Name":"TestExport","PortalName":"My Website"}""");
        try
        {
            IReadOnlyList<DnnModule> modules = DnnXmlParser.ParseExportJson(jsonFile);

            Assert.Single(modules);
            Assert.Equal("DNN_HTML", modules[0].ModuleName);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ParseExportFolder_SkinsAreIgnored_OnlyModulesReturned()
    {
        const string moduleXml = """
            <dotnetnuke type="Package" version="5.0">
              <packages>
                <package name="MyMod" type="Module" version="1.0.0">
                  <friendlyName>My Module</friendlyName>
                  <components>
                    <component type="Module">
                      <desktopModule><moduleName>MyMod</moduleName></desktopModule>
                    </component>
                  </components>
                </package>
              </packages>
            </dotnetnuke>
            """;

        // Build a folder that also contains a Skin_ entry (should be skipped)
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        using var innerStream = new MemoryStream();
        using (var innerZip = new ZipArchive(innerStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry dnnEntry = innerZip.CreateEntry("mymod.dnn");
            using StreamWriter w = new(dnnEntry.Open());
            w.Write(moduleXml);
        }

        string outerPath = Path.Combine(dir, "export_packages.zip");
        using (var outerZip = ZipFile.Open(outerPath, ZipArchiveMode.Create))
        {
            // Module entry
            ZipArchiveEntry modEntry = outerZip.CreateEntry("Module_MyMod_1.0.0.resources");
            using (Stream dest = modEntry.Open())
            {
                innerStream.Position = 0;
                innerStream.CopyTo(dest);
            }

            // Skin entry (should be skipped)
            ZipArchiveEntry skinEntry = outerZip.CreateEntry("Skin_MySkin_1.0.0.resources");
            using Stream skinDest = skinEntry.Open(); // empty
        }

        try
        {
            IReadOnlyList<DnnModule> modules = DnnXmlParser.ParseExportFolder(dir);

            Assert.Single(modules);
            Assert.Equal("MyMod", modules[0].ModuleName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Error handling
    // ------------------------------------------------------------------

    [Fact]
    public void ParseXml_EmptyString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => DnnXmlParser.ParseXml(string.Empty));
    }

    [Fact]
    public void ParseXml_InvalidXml_ThrowsInvalidOperationException()
    {
        Assert.Throws<System.Xml.XmlException>(() => DnnXmlParser.ParseXml("<not valid xml"));
    }

    [Fact]
    public void ParseXml_UnknownRootElement_ThrowsInvalidOperationException()
    {
        const string xml = "<unknownRoot><child/></unknownRoot>";
        Assert.Throws<InvalidOperationException>(() => DnnXmlParser.ParseXml(xml));
    }

    // ------------------------------------------------------------------
    // ParsePortalName
    // ------------------------------------------------------------------

    [Fact]
    public void ParsePortalName_ReadsPortalNameFromFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "export.json"),
                """{"PortalName":"My Test Site","ExportVersion":"1"}""");

            string? result = DnnXmlParser.ParsePortalName(dir);
            Assert.Equal("My Test Site", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ParsePortalName_ReadsPortalNameFromJsonFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"PortalName":"Direct File","Other":"ignored"}""");

            string? result = DnnXmlParser.ParsePortalName(path);
            Assert.Equal("Direct File", result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParsePortalName_ReturnsNullWhenFileDoesNotExist()
    {
        string? result = DnnXmlParser.ParsePortalName("/nonexistent/path");
        Assert.Null(result);
    }

    [Fact]
    public void ParsePortalName_ReturnsNullWhenPropertyMissing()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"Other":"value"}""");
            string? result = DnnXmlParser.ParsePortalName(path);
            Assert.Null(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParsePortalName_ReturnsNullOnCorruptJson()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not-json");
            string? result = DnnXmlParser.ParsePortalName(path);
            Assert.Null(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------
    // ParseHtmlContents / ExtractHtmlBodyFromDnnXml tests
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractHtmlBodyFromDnnXml_DecodesHtmlEntities()
    {
        const string xmlContent = """
            <htmltext><content><![CDATA[&lt;h1&gt;Hello&lt;/h1&gt;]]></content></htmltext>
            """;

        string? result = DnnXmlParser.ExtractHtmlBodyFromDnnXml(xmlContent);

        Assert.NotNull(result);
        Assert.Equal("<h1>Hello</h1>", result);
    }

    [Fact]
    public void ExtractHtmlBodyFromDnnXml_ReturnsNullOnEmptyContent()
    {
        const string xmlContent = """
            <htmltext><content><![CDATA[   ]]></content></htmltext>
            """;

        string? result = DnnXmlParser.ExtractHtmlBodyFromDnnXml(xmlContent);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractHtmlBodyFromDnnXml_ReturnsNullOnInvalidXml()
    {
        string? result = DnnXmlParser.ExtractHtmlBodyFromDnnXml("not-valid-xml");

        Assert.Null(result);
    }

    [Fact]
    public void ExtractHtmlBodyFromDnnXml_DecodesQuotesAndAmpersands()
    {
        const string xmlContent = """
            <htmltext><content><![CDATA[&lt;p class=&quot;x&quot;&gt;a &amp; b&lt;/p&gt;]]></content></htmltext>
            """;

        string? result = DnnXmlParser.ExtractHtmlBodyFromDnnXml(xmlContent);

        Assert.NotNull(result);
        Assert.Equal("<p class=\"x\">a & b</p>", result);
    }

    [Fact]
    public void ExtractHtmlBodyFromDnnXml_ReplacesPortalRootToken()
    {
        // {{PortalRoot}} is a DNN runtime token that expands to "/Portals/{id}/" at
        // run time.  After migration to DotCMS, portal files live at the site root,
        // so the token must be replaced with "/" to produce a valid relative URL.
        const string xmlContent = """
            <htmltext><content><![CDATA[&lt;img src=&quot;{{PortalRoot}}Images/logo.png&quot; /&gt;]]></content></htmltext>
            """;

        string? result = DnnXmlParser.ExtractHtmlBodyFromDnnXml(xmlContent);

        Assert.NotNull(result);
        Assert.DoesNotContain("{{PortalRoot}}", result);
        Assert.Contains("/Images/logo.png", result);
    }

    [Fact]
    public void ExtractHtmlBodyFromDnnXml_ReplacesPortalRootToken_CaseInsensitive()
    {
        // The token replacement should be case-insensitive.
        const string xmlContent = """
            <htmltext><content><![CDATA[&lt;a href=&quot;{{portalroot}}contact.aspx&quot;&gt;Contact&lt;/a&gt;]]></content></htmltext>
            """;

        string? result = DnnXmlParser.ExtractHtmlBodyFromDnnXml(xmlContent);

        Assert.NotNull(result);
        Assert.DoesNotContain("{{portalroot}}", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/contact.aspx", result);
    }

    [Fact]
    public void ParseHtmlContents_ReturnsEmptyWhenNoExportDb()
    {
        string tempDir = Path.GetTempFileName();
        File.Delete(tempDir);
        Directory.CreateDirectory(tempDir);
        try
        {
            // No export_db.zip in the folder → should return empty list, not throw.
            IReadOnlyList<DnnHtmlContent> result =
                DnnXmlParser.ParseHtmlContents(tempDir);
            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
