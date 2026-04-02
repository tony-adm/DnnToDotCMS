using System.IO.Compression;
using DnnToDotCms.Mappings;
using DnnToDotCms.Models;
using DnnToDotCms.Parser;
using LiteDB;

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
        // run time.  After migration to DotCMS, the token is replaced with "/" so
        // that image URLs match the DotCMS FileAsset folder structure.
        const string xmlContent = """
            <htmltext><content><![CDATA[&lt;img src=&quot;{{PortalRoot}}Images/logo.png&quot; /&gt;]]></content></htmltext>
            """;

        string? result = DnnXmlParser.ExtractHtmlBodyFromDnnXml(xmlContent);

        Assert.NotNull(result);
        Assert.DoesNotContain("{{PortalRoot}}", result);
        Assert.Contains("/Images/logo.png", result);
    }

    [Fact]
    public void ExtractHtmlBodyFromDnnXml_NonImagesPortalRootReplacedWithSlash()
    {
        // {{PortalRoot}} tokens for non-Images paths (e.g. root-level CSS) must
        // still be replaced with "/" to produce a valid site-root relative URL.
        const string xmlContent = """
            <htmltext><content><![CDATA[&lt;link href=&quot;{{PortalRoot}}home.css&quot; /&gt;]]></content></htmltext>
            """;

        string? result = DnnXmlParser.ExtractHtmlBodyFromDnnXml(xmlContent);

        Assert.NotNull(result);
        Assert.DoesNotContain("{{PortalRoot}}", result);
        Assert.Contains("/home.css", result);
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
    public void ExtractHtmlBodyFromDnnXml_ImagesPortalRootReplacedWithSlash()
    {
        // {{PortalRoot}}Images/ is replaced with /Images/ so that HTML content
        // links match the DotCMS FileAsset folder path.
        const string xmlContent = """
            <htmltext><content><![CDATA[&lt;img src=&quot;{{PortalRoot}}Images/banner.jpg&quot; /&gt;]]></content></htmltext>
            """;

        string? result = DnnXmlParser.ExtractHtmlBodyFromDnnXml(xmlContent);

        Assert.NotNull(result);
        Assert.DoesNotContain("{{PortalRoot}}", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/Images/banner.jpg", result);
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

    // ------------------------------------------------------------------
    // ParseHtmlContents – placeholder for non-HTML modules
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a temp folder with an export_db.zip containing a LiteDB
    /// database populated by the supplied <paramref name="populate"/> action.
    /// Returns the temp folder path; the caller must delete it when done.
    /// </summary>
    private static string BuildExportDbFolder(Action<LiteDatabase> populate)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string dbFile = Path.Combine(tempDir, "temp.dnndb");
        using (var db = new LiteDatabase($"Filename={dbFile}"))
        {
            populate(db);
        }

        string zipPath = Path.Combine(tempDir, "export_db.zip");
        using (var zipStream = File.Create(zipPath))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(dbFile, "export.dnndb");
        }
        File.Delete(dbFile);

        return tempDir;
    }

    [Fact]
    public void ParseHtmlContents_CreatesPlaceholderForModuleWithoutContent()
    {
        // A module (FisSlider) exists in ExportTabModule but has no entry
        // in ExportModuleContent.  ParseHtmlContents must create a
        // placeholder contentlet for it.
        string tempDir = BuildExportDbFolder(db =>
        {
            var tabs = db.GetCollection("ExportTab");
            tabs.Insert(new BsonDocument
            {
                ["TabID"]    = 1,
                ["UniqueId"] = new BsonValue(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
            });

            var tabModules = db.GetCollection("ExportTabModule");
            tabModules.Insert(new BsonDocument
            {
                ["ModuleID"]     = 42,
                ["TabID"]        = 1,
                ["ModuleTitle"]  = "Banner Slideshow",
                ["PaneName"]     = "ContentPane",
                ["ContainerSrc"] = "[L]Containers/FBOT/slider.ascx",
                ["IconFile"]     = "",
            });

            var modules = db.GetCollection("ExportModule");
            modules.Insert(new BsonDocument
            {
                ["ModuleID"]     = 42,
                ["FriendlyName"] = "FisSlider",
            });

            // No ExportModuleContent entry for ModuleID=42.
        });

        try
        {
            IReadOnlyList<DnnHtmlContent> result =
                DnnXmlParser.ParseHtmlContents(tempDir);

            Assert.Single(result);
            DnnHtmlContent hc = result[0];
            Assert.Equal("Banner Slideshow", hc.Title);
            Assert.Contains("dnn-module-placeholder", hc.HtmlBody);
            Assert.Contains("FisSlider", hc.HtmlBody);
            Assert.Contains("Banner Slideshow", hc.HtmlBody);
            Assert.Contains("recreated in DotCMS", hc.HtmlBody);
            Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", hc.TabUniqueId);
            Assert.Equal("ContentPane", hc.PaneName);
            Assert.Equal("[L]Containers/FBOT/slider.ascx", hc.ContainerSrc);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParseHtmlContents_PlaceholderUsesCustomModuleFriendlyName()
    {
        // When ExportModule has no FriendlyName, the placeholder falls
        // back to "Custom Module".
        string tempDir = BuildExportDbFolder(db =>
        {
            var tabs = db.GetCollection("ExportTab");
            tabs.Insert(new BsonDocument
            {
                ["TabID"]    = 1,
                ["UniqueId"] = new BsonValue(Guid.Parse("11111111-2222-3333-4444-555555555555")),
            });

            var tabModules = db.GetCollection("ExportTabModule");
            tabModules.Insert(new BsonDocument
            {
                ["ModuleID"]    = 99,
                ["TabID"]       = 1,
                ["ModuleTitle"] = "My Widget",
                ["PaneName"]    = "SidePane",
            });

            // No ExportModule entry for ModuleID=99 → no FriendlyName.
        });

        try
        {
            IReadOnlyList<DnnHtmlContent> result =
                DnnXmlParser.ParseHtmlContents(tempDir);

            Assert.Single(result);
            Assert.Contains("Custom Module", result[0].HtmlBody);
            Assert.Contains("My Widget", result[0].HtmlBody);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParseHtmlContents_MixesHtmlAndPlaceholderModules()
    {
        // Two modules on the same tab: one HTML module with content,
        // one custom module without content.  Both should appear in results.
        string tempDir = BuildExportDbFolder(db =>
        {
            var tabs = db.GetCollection("ExportTab");
            tabs.Insert(new BsonDocument
            {
                ["TabID"]    = 1,
                ["UniqueId"] = new BsonValue(Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444")),
            });

            var tabModules = db.GetCollection("ExportTabModule");
            tabModules.Insert(new BsonDocument
            {
                ["ModuleID"]    = 10,
                ["TabID"]       = 1,
                ["ModuleTitle"] = "Welcome Text",
                ["PaneName"]    = "ContentPane",
            });
            tabModules.Insert(new BsonDocument
            {
                ["ModuleID"]    = 20,
                ["TabID"]       = 1,
                ["ModuleTitle"] = "Image Carousel",
                ["PaneName"]    = "BannerPane",
            });

            var moduleContents = db.GetCollection("ExportModuleContent");
            moduleContents.Insert(new BsonDocument
            {
                ["ModuleID"]   = 10,
                ["XmlContent"] = "<htmltext><content><![CDATA[&lt;p&gt;Hello!&lt;/p&gt;]]></content></htmltext>",
            });

            var modules = db.GetCollection("ExportModule");
            modules.Insert(new BsonDocument
            {
                ["ModuleID"]     = 20,
                ["FriendlyName"] = "ImageCarousel",
            });

            // No ExportModuleContent for ModuleID=20.
        });

        try
        {
            IReadOnlyList<DnnHtmlContent> result =
                DnnXmlParser.ParseHtmlContents(tempDir);

            Assert.Equal(2, result.Count);

            // The HTML module should have actual content.
            DnnHtmlContent htmlItem = result.First(r => r.Title == "Welcome Text");
            Assert.Equal("<p>Hello!</p>", htmlItem.HtmlBody);
            Assert.Equal("ContentPane", htmlItem.PaneName);

            // The custom module should have a placeholder.
            DnnHtmlContent placeholder = result.First(r => r.Title == "Image Carousel");
            Assert.Contains("dnn-module-placeholder", placeholder.HtmlBody);
            Assert.Contains("ImageCarousel", placeholder.HtmlBody);
            Assert.Equal("BannerPane", placeholder.PaneName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParseHtmlContents_PlaceholderPerTabForSharedModule()
    {
        // A custom module shared across two tabs should produce a
        // placeholder entry for each tab.
        string tempDir = BuildExportDbFolder(db =>
        {
            var tabs = db.GetCollection("ExportTab");
            tabs.Insert(new BsonDocument
            {
                ["TabID"]    = 1,
                ["UniqueId"] = new BsonValue(Guid.Parse("aaaa0001-0000-0000-0000-000000000000")),
            });
            tabs.Insert(new BsonDocument
            {
                ["TabID"]    = 2,
                ["UniqueId"] = new BsonValue(Guid.Parse("aaaa0002-0000-0000-0000-000000000000")),
            });

            var tabModules = db.GetCollection("ExportTabModule");
            tabModules.Insert(new BsonDocument
            {
                ["ModuleID"]    = 50,
                ["TabID"]       = 1,
                ["ModuleTitle"] = "Shared Slider",
                ["PaneName"]    = "ContentPane",
            });
            tabModules.Insert(new BsonDocument
            {
                ["ModuleID"]    = 50,
                ["TabID"]       = 2,
                ["ModuleTitle"] = "Shared Slider",
                ["PaneName"]    = "ContentPane",
            });

            var modules = db.GetCollection("ExportModule");
            modules.Insert(new BsonDocument
            {
                ["ModuleID"]     = 50,
                ["FriendlyName"] = "FisSlider",
            });
        });

        try
        {
            IReadOnlyList<DnnHtmlContent> result =
                DnnXmlParser.ParseHtmlContents(tempDir);

            Assert.Equal(2, result.Count);
            Assert.All(result, r =>
            {
                Assert.Contains("dnn-module-placeholder", r.HtmlBody);
                Assert.Contains("FisSlider", r.HtmlBody);
            });

            var tabIds = result.Select(r => r.TabUniqueId).ToHashSet();
            Assert.Contains("aaaa0001-0000-0000-0000-000000000000", tabIds);
            Assert.Contains("aaaa0002-0000-0000-0000-000000000000", tabIds);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParseHtmlContents_PlaceholderPreservesContainerSrcAndIconFile()
    {
        // Verify that ContainerSrc and IconFile from ExportTabModule are
        // carried through to the placeholder DnnHtmlContent entry.
        string tempDir = BuildExportDbFolder(db =>
        {
            var tabs = db.GetCollection("ExportTab");
            tabs.Insert(new BsonDocument
            {
                ["TabID"]    = 1,
                ["UniqueId"] = new BsonValue(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000")),
            });

            var tabModules = db.GetCollection("ExportTabModule");
            tabModules.Insert(new BsonDocument
            {
                ["ModuleID"]     = 77,
                ["TabID"]        = 1,
                ["ModuleTitle"]  = "Fancy Gallery",
                ["PaneName"]     = "TopPane",
                ["ContainerSrc"] = "[L]Containers/FBOT/gallery.ascx",
                ["IconFile"]     = "Images/gallery-icon.png",
            });

            var modules = db.GetCollection("ExportModule");
            modules.Insert(new BsonDocument
            {
                ["ModuleID"]     = 77,
                ["FriendlyName"] = "PhotoGallery",
            });
        });

        try
        {
            IReadOnlyList<DnnHtmlContent> result =
                DnnXmlParser.ParseHtmlContents(tempDir);

            Assert.Single(result);
            DnnHtmlContent hc = result[0];
            Assert.Equal("[L]Containers/FBOT/gallery.ascx", hc.ContainerSrc);
            Assert.Equal("Images/gallery-icon.png", hc.IconFile);
            Assert.Equal("TopPane", hc.PaneName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParseHtmlContents_ModuleWithContentNotDuplicated()
    {
        // A module that HAS ExportModuleContent should NOT get a
        // placeholder — only the real content entry should exist.
        string tempDir = BuildExportDbFolder(db =>
        {
            var tabs = db.GetCollection("ExportTab");
            tabs.Insert(new BsonDocument
            {
                ["TabID"]    = 1,
                ["UniqueId"] = new BsonValue(Guid.Parse("cccccccc-0000-0000-0000-000000000000")),
            });

            var tabModules = db.GetCollection("ExportTabModule");
            tabModules.Insert(new BsonDocument
            {
                ["ModuleID"]    = 30,
                ["TabID"]       = 1,
                ["ModuleTitle"] = "About Us",
                ["PaneName"]    = "ContentPane",
            });

            var moduleContents = db.GetCollection("ExportModuleContent");
            moduleContents.Insert(new BsonDocument
            {
                ["ModuleID"]   = 30,
                ["XmlContent"] = "<htmltext><content><![CDATA[&lt;p&gt;About text&lt;/p&gt;]]></content></htmltext>",
            });
        });

        try
        {
            IReadOnlyList<DnnHtmlContent> result =
                DnnXmlParser.ParseHtmlContents(tempDir);

            Assert.Single(result);
            Assert.Equal("<p>About text</p>", result[0].HtmlBody);
            Assert.DoesNotContain("dnn-module-placeholder", result[0].HtmlBody);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
