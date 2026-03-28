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
}
