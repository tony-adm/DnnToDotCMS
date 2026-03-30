using System.Text.Json;
using DnnToDotCms.Converter;
using DnnToDotCms.Models;

namespace DnnToDotCms.Tests;

public class DnnConverterTests
{
    private static DnnModule MakeModule(string name, string? description = null) =>
        new(name, FriendlyName: name, Description: description ?? string.Empty);

    // ------------------------------------------------------------------
    // Single module conversion
    // ------------------------------------------------------------------

    [Fact]
    public void Convert_HtmlModule_ReturnsHtmlContentType()
    {
        DotCmsContentType ct = DnnConverter.Convert(MakeModule("HTML"));

        Assert.Equal("htmlContent", ct.Variable);
        Assert.Equal("HTMLContent", ct.Name);
        Assert.Equal("com.dotcms.contenttype.model.type.SimpleContentType", ct.Clazz);
    }

    [Fact]
    public void Convert_EventsModule_ReturnsEventContentType()
    {
        DotCmsContentType ct = DnnConverter.Convert(MakeModule("Events"));

        Assert.Equal("event", ct.Variable);
        Assert.Equal("Event", ct.Name);
    }

    [Fact]
    public void Convert_AppendsModuleDescriptionToContentTypeDescription()
    {
        const string extra = "Our company events calendar";
        DotCmsContentType ct = DnnConverter.Convert(MakeModule("Events", extra));

        Assert.Contains(extra, ct.Description);
    }

    [Fact]
    public void Convert_DoesNotDuplicateDescriptionWhenAlreadyPresent()
    {
        // The default mapping description already contains "Events"
        DotCmsContentType ct = DnnConverter.Convert(MakeModule("Events", "Converted from DNN Events module"));

        // Should not result in double text
        int count = ct.Description.Split("Converted from DNN Events module").Length - 1;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Convert_UnknownModule_ReturnsFallbackContentType()
    {
        DotCmsContentType ct = DnnConverter.Convert(MakeModule("WidgetPro"));

        Assert.Equal("WidgetPro", ct.Name);
        Assert.Equal("widgetPro", ct.Variable);
        Assert.True(ct.Fields.Count >= 2);
    }

    // ------------------------------------------------------------------
    // Bulk conversion / de-duplication
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertAll_DuplicateModuleTypes_ReturnsSingleContentType()
    {
        var modules = new[]
        {
            MakeModule("HTML"),
            MakeModule("HTML"),
            MakeModule("html"),   // same type, different casing
        };

        IReadOnlyList<DotCmsContentType> result = DnnConverter.ConvertAll(modules);

        Assert.Single(result);
        Assert.Equal("htmlContent", result[0].Variable);
    }

    [Fact]
    public void ConvertAll_MultipleDistinctTypes_ReturnsOneEach()
    {
        var modules = new[]
        {
            MakeModule("HTML"),
            MakeModule("Events"),
            MakeModule("Blog"),
        };

        IReadOnlyList<DotCmsContentType> result = DnnConverter.ConvertAll(modules);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, ct => ct.Variable == "htmlContent");
        Assert.Contains(result, ct => ct.Variable == "event");
        Assert.Contains(result, ct => ct.Variable == "blogPost");
    }

    [Fact]
    public void ConvertAll_EmptyList_ReturnsEmpty()
    {
        IReadOnlyList<DotCmsContentType> result = DnnConverter.ConvertAll([]);

        Assert.Empty(result);
    }

    // ------------------------------------------------------------------
    // VARCHAR(255) truncation tests
    // ------------------------------------------------------------------

    [Fact]
    public void Convert_LongModuleDescription_TruncatesDescriptionTo255()
    {
        // A module description that when combined with the base content-type
        // description would exceed 255 characters.
        string longDesc = new string('x', 300);
        DotCmsContentType ct = DnnConverter.Convert(MakeModule("Events", longDesc));

        Assert.True(ct.Description.Length <= 255,
            $"Description length {ct.Description.Length} exceeds 255 characters.");
    }

    [Fact]
    public void Convert_ModuleDescriptionExactly255_IsNotTruncated()
    {
        // When the combined description is exactly 255 characters it should
        // be kept intact.
        string desc = new string('a', 255);
        // Use an unknown module so the base description is empty and we get
        // exactly 255 chars without any appended separator.
        DotCmsContentType ct = DnnConverter.Convert(MakeModule("UnknownModuleType", desc));

        Assert.Equal(255, ct.Description.Length);
    }

    // ------------------------------------------------------------------
    // JSON serialisation
    // ------------------------------------------------------------------

    [Fact]
    public void Convert_Serialises_ToValidJson()
    {
        DotCmsContentType ct = DnnConverter.Convert(MakeModule("HTML"));
        string json = JsonSerializer.Serialize(ct);

        Assert.Contains("\"clazz\"", json);
        Assert.Contains("\"name\"",  json);
        Assert.Contains("\"fields\"", json);
    }

    [Fact]
    public void Convert_Serialises_FieldsHaveExpectedProperties()
    {
        DotCmsContentType ct = DnnConverter.Convert(MakeModule("HTML"));
        string json = JsonSerializer.Serialize(ct,
            new JsonSerializerOptions { WriteIndented = false });

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement fields = doc.RootElement.GetProperty("fields");

        Assert.True(fields.GetArrayLength() > 0);

        JsonElement first = fields[0];
        Assert.True(first.TryGetProperty("clazz",    out _));
        Assert.True(first.TryGetProperty("name",     out _));
        Assert.True(first.TryGetProperty("variable", out _));
    }
}
