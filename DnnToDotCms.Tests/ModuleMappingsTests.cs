using DnnToDotCms.Mappings;
using DnnToDotCms.Models;

namespace DnnToDotCms.Tests;

public class ModuleMappingsTests
{
    [Theory]
    [InlineData("HTML",              "htmlContent",       "HTMLContent")]
    [InlineData("html",              "htmlContent",       "HTMLContent")]
    [InlineData("Text/HTML",         "htmlContent",       "HTMLContent")]
    [InlineData("Events",            "event",             "Event")]
    [InlineData("DNNEvents",         "event",             "Event")]
    [InlineData("FAQs",              "faq",               "FAQ")]
    [InlineData("faq",               "faq",               "FAQ")]
    [InlineData("Announcements",     "announcement",      "Announcement")]
    [InlineData("Blog",              "blogPost",          "BlogPost")]
    [InlineData("Documents",         "document",          "Document")]
    [InlineData("DocumentLibrary",   "document",          "Document")]
    [InlineData("Links",             "link",              "Link")]
    [InlineData("Contacts",          "contact",           "Contact")]
    [InlineData("NewsFeed",          "newsItem",          "NewsItem")]
    [InlineData("news",              "newsItem",          "NewsItem")]
    [InlineData("Gallery",           "galleryItem",       "GalleryItem")]
    [InlineData("Feedback",          "feedback",          "Feedback")]
    [InlineData("Forms",             "formSubmission",    "FormSubmission")]
    public void GetContentType_KnownModules_ReturnCorrectMapping(
        string moduleName, string expectedVariable, string expectedName)
    {
        DotCmsContentType ct = ModuleMappings.GetContentType(moduleName);

        Assert.Equal(expectedVariable, ct.Variable);
        Assert.Equal(expectedName,     ct.Name);
    }

    [Fact]
    public void GetContentType_UnknownModule_ReturnsFallbackWithModuleName()
    {
        DotCmsContentType ct = ModuleMappings.GetContentType("MyCustomModule");

        Assert.Equal("MyCustomModule", ct.Name);
        Assert.Equal("myCustomModule", ct.Variable);
        Assert.Contains("Title",   ct.Fields.Select(f => f.Name));
        Assert.Contains("Content", ct.Fields.Select(f => f.Name));
    }

    [Fact]
    public void GetContentType_UnknownEmptyName_ReturnsFallback()
    {
        DotCmsContentType ct = ModuleMappings.GetContentType(string.Empty);

        Assert.Equal("GenericModule", ct.Name);
        Assert.NotEmpty(ct.Fields);
    }

    [Theory]
    [InlineData("Member Directory",                          "MemberDirectory")]
    [InlineData("My-Module",                                 "MyModule")]
    [InlineData("My Module - Content",                       "MyModuleContent")]
    [InlineData("DotNetNuke.Modules.MemberDirectory",        "DotNetNukeModulesMemberDirectory")]
    [InlineData("DotNetNuke.Modules.CoreMessaging",          "DotNetNukeModulesCoreMessaging")]
    [InlineData("Resource Manager",                          "ResourceManager")]
    [InlineData("- LeadingDash",                             "LeadingDash")]
    [InlineData("-  - Multiple Dashes",                      "MultipleDashes")]
    [InlineData("  -  SpacesAndDash  ",                      "SpacesAndDash")]
    public void GetContentType_FallbackName_HasNoSpacesOrHyphens(
        string moduleName, string expectedName)
    {
        DotCmsContentType ct = ModuleMappings.GetContentType(moduleName);

        Assert.Equal(expectedName, ct.Name);
        Assert.DoesNotContain(" ",  ct.Name);
        Assert.DoesNotContain("-",  ct.Name);
        Assert.DoesNotContain(".",  ct.Name);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("   ")]
    [InlineData("-")]
    public void GetContentType_ModuleNameOnlySpecialChars_ReturnsFallback(string moduleName)
    {
        DotCmsContentType ct = ModuleMappings.GetContentType(moduleName);

        Assert.Equal("GenericModule", ct.Name);
    }

    [Fact]
    public void GetContentType_HtmlModule_HasTitleAndBodyFields()
    {
        DotCmsContentType ct = ModuleMappings.GetContentType("HTML");

        DotCmsField? titleField = ct.Fields.FirstOrDefault(f => f.Variable == "title");
        DotCmsField? bodyField  = ct.Fields.FirstOrDefault(f => f.Variable == "body");

        Assert.NotNull(titleField);
        Assert.NotNull(bodyField);
        Assert.True(titleField!.Required);
        Assert.True(titleField.Listed);
        Assert.Equal("com.dotcms.contenttype.model.field.TextField",   titleField.Clazz);
        Assert.Equal("com.dotcms.contenttype.model.field.WysiwygField", bodyField!.Clazz);
    }

    [Fact]
    public void GetContentType_EventModule_HasStartDateAndEndDateFields()
    {
        DotCmsContentType ct = ModuleMappings.GetContentType("Events");

        Assert.Contains(ct.Fields, f => f.Variable == "startDate");
        Assert.Contains(ct.Fields, f => f.Variable == "endDate");
        Assert.Contains(ct.Fields, f => f.Variable == "location");
    }

    [Fact]
    public void GetContentType_BlogModule_HasStatusSelectField()
    {
        DotCmsContentType ct = ModuleMappings.GetContentType("Blog");

        DotCmsField? statusField = ct.Fields.FirstOrDefault(f => f.Variable == "status");
        Assert.NotNull(statusField);
        Assert.Equal("com.dotcms.contenttype.model.field.SelectField", statusField!.Clazz);
        Assert.NotNull(statusField.Values);
        Assert.Contains("Published", statusField.Values);
    }

    [Fact]
    public void GetContentType_AllFields_HaveNonEmptyClazzAndVariable()
    {
        string[] knownModules = ["HTML", "Events", "FAQs", "Blog", "Forms",
                                 "Announcements", "Documents", "Links",
                                 "Contacts", "NewsFeed", "Gallery", "Feedback"];

        foreach (string module in knownModules)
        {
            DotCmsContentType ct = ModuleMappings.GetContentType(module);
            foreach (DotCmsField field in ct.Fields)
            {
                Assert.False(string.IsNullOrWhiteSpace(field.Clazz),
                    $"{module}.{field.Name}: Clazz must not be empty");
                Assert.False(string.IsNullOrWhiteSpace(field.Variable),
                    $"{module}.{field.Name}: Variable must not be empty");
            }
        }
    }

    [Theory]
    [InlineData("HTML",     "html")]
    [InlineData("My Module","mymodule")]
    [InlineData("DNN_HTML", "dnnhtml")]
    public void Normalise_ReturnsLowercaseWithoutSpaces(string input, string expected)
    {
        Assert.Equal(expected, ModuleMappings.Normalise(input));
    }
}
