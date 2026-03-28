using System.Text.RegularExpressions;
using DnnToDotCms.Models;

namespace DnnToDotCms.Mappings;

/// <summary>
/// Default mappings from common DNN module types to DotCMS content-type
/// definitions.  Each entry maps a normalised DNN module name (lower-cased,
/// with spaces/underscores/hyphens removed) to a factory that produces a
/// <see cref="DotCmsContentType"/> instance.
/// </summary>
public static class ModuleMappings
{
    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Return a <see cref="DotCmsContentType"/> template for the given DNN
    /// module name.  When no specific mapping exists for <paramref name="moduleName"/>
    /// the lookup is retried against <paramref name="friendlyName"/> before
    /// falling back to a generic HTML content type.
    /// </summary>
    public static DotCmsContentType GetContentType(string moduleName, string? friendlyName = null)
    {
        string key = Normalise(moduleName);
        if (Mappings.TryGetValue(key, out Func<DotCmsContentType>? factory))
            return factory();

        // Retry with the friendly name (e.g. module "DNN_HTML" / friendly "HTML")
        if (!string.IsNullOrWhiteSpace(friendlyName))
        {
            string friendlyKey = Normalise(friendlyName);
            if (Mappings.TryGetValue(friendlyKey, out factory))
                return factory();
        }

        // Generic fallback: create a simple HTML content type named after the module
        string safe = string.IsNullOrWhiteSpace(moduleName) ? "GenericModule" : moduleName.Trim();
        string variable = ToCamelCase(safe);
        return new DotCmsContentType
        {
            Name        = safe,
            Variable    = variable,
            Description = $"Converted from DNN {safe} module",
            Icon        = "fa fa-cube",
            Fields      =
            [
                TextField("Title",       "title",   required: true, listed: true),
                WysiwygField("Content",  "content"),
            ]
        };
    }

    /// <summary>Normalise a module name for lookup.</summary>
    public static string Normalise(string name) =>
        Regex.Replace(name.ToLowerInvariant(), @"[\s_\-/]+", string.Empty);

    // ------------------------------------------------------------------
    // Module-to-content-type mapping table
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, Func<DotCmsContentType>> Mappings = new()
    {
        // HTML / Text-HTML
        ["html"]     = HtmlContent,
        ["texthtml"] = HtmlContent,

        // Announcements
        ["announcements"] = Announcement,

        // Events / Calendar
        ["events"]    = Event,
        ["dnnevents"] = Event,

        // FAQs
        ["faqs"] = Faq,
        ["faq"]  = Faq,

        // Forms
        ["forms"]      = FormSubmission,
        ["forminputs"] = FormSubmission,

        // Blog
        ["blog"] = BlogPost,

        // Documents
        ["documents"]       = Document,
        ["documentlibrary"] = Document,

        // Links
        ["links"] = Link,

        // Contacts
        ["contacts"] = Contact,

        // News Feed
        ["newsfeed"] = NewsItem,
        ["news"]     = NewsItem,

        // Gallery
        ["gallery"] = GalleryItem,

        // Feedback
        ["feedback"] = Feedback,
    };

    // ------------------------------------------------------------------
    // Content-type factories
    // ------------------------------------------------------------------

    private static DotCmsContentType HtmlContent() => new()
    {
        Name        = "HTMLContent",
        Variable    = "htmlContent",
        Description = "Converted from DNN HTML module",
        Icon        = "fa fa-code",
        Fields      =
        [
            TextField("Title",   "title",   required: true, listed: true),
            WysiwygField("Body", "body",     required: true),
        ]
    };

    private static DotCmsContentType Announcement() => new()
    {
        Name        = "Announcement",
        Variable    = "announcement",
        Description = "Converted from DNN Announcements module",
        Icon        = "fa fa-bullhorn",
        Fields      =
        [
            TextField("Title",        "title",       required: true, listed: true),
            WysiwygField("Description", "description"),
            DateField("Publish Date", "publishDate",  listed: true),
            DateField("Expire Date",  "expireDate"),
            TextField("URL",          "url"),
            ImageField("Image",       "image"),
        ]
    };

    private static DotCmsContentType Event() => new()
    {
        Name        = "Event",
        Variable    = "event",
        Description = "Converted from DNN Events module",
        Icon        = "fa fa-calendar",
        Fields      =
        [
            TextField("Event Name",    "eventName",  required: true, listed: true),
            DateTimeField("Start Date","startDate",   required: true, listed: true),
            DateTimeField("End Date",  "endDate"),
            TextField("Location",      "location"),
            WysiwygField("Description","description"),
            CheckboxField("All Day Event", "allDayEvent"),
            TextField("Category",      "category"),
            ImageField("Event Image",  "eventImage"),
        ]
    };

    private static DotCmsContentType Faq() => new()
    {
        Name        = "FAQ",
        Variable    = "faq",
        Description = "Converted from DNN FAQs module",
        Icon        = "fa fa-question-circle",
        Fields      =
        [
            TextField("Question",     "question",    required: true, listed: true),
            WysiwygField("Answer",    "answer",       required: true),
            TextField("Category",     "category"),
            DateField("Created Date", "createdDate"),
        ]
    };

    private static DotCmsContentType FormSubmission() => new()
    {
        Name        = "FormSubmission",
        Variable    = "formSubmission",
        Description = "Converted from DNN Forms module",
        Icon        = "fa fa-wpforms",
        Fields      =
        [
            TextField("Form Name",       "formName",       required: true, listed: true),
            TextField("Submitter Name",  "submitterName"),
            TextField("Submitter Email", "submitterEmail"),
            WysiwygField("Message",      "message"),
            DateTimeField("Submitted At","submittedAt",    listed: true),
        ]
    };

    private static DotCmsContentType BlogPost() => new()
    {
        Name        = "BlogPost",
        Variable    = "blogPost",
        Description = "Converted from DNN Blog module",
        Icon        = "fa fa-rss",
        Fields      =
        [
            TextField("Title",         "title",         required: true, listed: true),
            WysiwygField("Body",       "body",           required: true),
            TextField("Author",        "author",         listed: true),
            DateField("Publish Date",  "publishDate",    listed: true),
            TextField("Tags",          "tags",
                hint: "Comma-separated list of tags"),
            SelectField("Status",      "status",
                "Draft|Draft\nPublished|Published\nArchived|Archived",
                required: true),
            ImageField("Featured Image", "featuredImage"),
        ]
    };

    private static DotCmsContentType Document() => new()
    {
        Name        = "Document",
        Variable    = "document",
        Description = "Converted from DNN Document Library module",
        Icon        = "fa fa-file",
        Fields      =
        [
            TextField("Title",        "title",       required: true, listed: true),
            FileField("File",         "file"),
            WysiwygField("Description","description"),
            TextField("Category",     "category"),
            DateField("Created Date", "createdDate", listed: true),
            TextField("Owner",        "owner"),
        ]
    };

    private static DotCmsContentType Link() => new()
    {
        Name        = "Link",
        Variable    = "link",
        Description = "Converted from DNN Links module",
        Icon        = "fa fa-link",
        Fields      =
        [
            TextField("Title",         "title",  required: true, listed: true),
            TextField("URL",           "url",    required: true),
            WysiwygField("Description","description"),
            SelectField("Target",      "target",
                "_blank|New Window\n_self|Same Window"),
            DateField("Created Date",  "createdDate"),
        ]
    };

    private static DotCmsContentType Contact() => new()
    {
        Name        = "Contact",
        Variable    = "contact",
        Description = "Converted from DNN Contacts module",
        Icon        = "fa fa-address-book",
        Fields      =
        [
            TextField("Full Name",  "fullName",  required: true, listed: true),
            TextField("Email",      "email"),
            TextField("Phone",      "phone"),
            TextField("Company",    "company",   listed: true),
            TextField("Job Title",  "jobTitle"),
            WysiwygField("Notes",   "notes"),
            ImageField("Photo",     "photo"),
        ]
    };

    private static DotCmsContentType NewsItem() => new()
    {
        Name        = "NewsItem",
        Variable    = "newsItem",
        Description = "Converted from DNN News Feed module",
        Icon        = "fa fa-newspaper-o",
        Fields      =
        [
            TextField("Headline",       "headline",      required: true, listed: true),
            WysiwygField("Body",        "body"),
            TextField("Source",         "source"),
            TextField("Source URL",     "sourceUrl"),
            DateField("Published Date", "publishedDate", listed: true),
            ImageField("Image",         "image"),
        ]
    };

    private static DotCmsContentType GalleryItem() => new()
    {
        Name        = "GalleryItem",
        Variable    = "galleryItem",
        Description = "Converted from DNN Gallery module",
        Icon        = "fa fa-image",
        Fields      =
        [
            TextField("Title",          "title",   required: true, listed: true),
            WysiwygField("Caption",     "caption"),
            ImageField("Image",         "image"),
            TextField("Album",          "album"),
            DateField("Date Taken",     "dateTaken"),
        ]
    };

    private static DotCmsContentType Feedback() => new()
    {
        Name        = "Feedback",
        Variable    = "feedback",
        Description = "Converted from DNN Feedback module",
        Icon        = "fa fa-comment",
        Fields      =
        [
            TextField("Name",           "name",        required: true),
            TextField("Email",          "email",       required: true),
            TextField("Subject",        "subject",     required: true, listed: true),
            WysiwygField("Message",     "message",     required: true),
            DateTimeField("Submitted At","submittedAt",listed: true),
        ]
    };


    // ------------------------------------------------------------------
    // Field builder helpers
    // ------------------------------------------------------------------

    private static DotCmsField TextField(
        string name, string variable,
        bool required = false, bool listed = false, string? hint = null) => new()
    {
        Clazz          = "com.dotcms.contenttype.model.field.TextField",
        Name           = name,
        Variable       = variable,
        DataType       = "TEXT",
        FieldTypeLabel = "Text",
        Indexed        = true,
        Required       = required,
        Searchable     = true,
        Sortable       = true,
        Listed         = listed,
        Hint           = hint,
    };

    private static DotCmsField WysiwygField(
        string name, string variable, bool required = false) => new()
    {
        Clazz          = "com.dotcms.contenttype.model.field.WysiwygField",
        Name           = name,
        Variable       = variable,
        DataType       = "LONG_TEXT",
        FieldTypeLabel = "WYSIWYG",
        Indexed        = true,
        Required       = required,
        Searchable     = true,
    };

    private static DotCmsField DateField(
        string name, string variable,
        bool required = false, bool listed = false) => new()
    {
        Clazz          = "com.dotcms.contenttype.model.field.DateField",
        Name           = name,
        Variable       = variable,
        DataType       = "DATE",
        FieldTypeLabel = "Date",
        Indexed        = true,
        Required       = required,
        Sortable       = true,
        Listed         = listed,
    };

    private static DotCmsField DateTimeField(
        string name, string variable,
        bool required = false, bool listed = false) => new()
    {
        Clazz          = "com.dotcms.contenttype.model.field.DateTimeField",
        Name           = name,
        Variable       = variable,
        DataType       = "DATE",
        FieldTypeLabel = "Date and Time",
        Indexed        = true,
        Required       = required,
        Sortable       = true,
        Listed         = listed,
    };

    private static DotCmsField SelectField(
        string name, string variable, string values,
        bool required = false) => new()
    {
        Clazz          = "com.dotcms.contenttype.model.field.SelectField",
        Name           = name,
        Variable       = variable,
        DataType       = "TEXT",
        FieldTypeLabel = "Select",
        Indexed        = true,
        Required       = required,
        Searchable     = true,
        Sortable       = true,
        Values         = values,
    };

    private static DotCmsField FileField(string name, string variable) => new()
    {
        Clazz          = "com.dotcms.contenttype.model.field.FileField",
        Name           = name,
        Variable       = variable,
        DataType       = "SYSTEM",
        FieldTypeLabel = "File",
        Indexed        = false,
        Searchable     = false,
    };

    private static DotCmsField ImageField(string name, string variable) => new()
    {
        Clazz          = "com.dotcms.contenttype.model.field.ImageField",
        Name           = name,
        Variable       = variable,
        DataType       = "SYSTEM",
        FieldTypeLabel = "Image",
        Indexed        = false,
        Searchable     = false,
    };

    private static DotCmsField CheckboxField(string name, string variable) => new()
    {
        Clazz          = "com.dotcms.contenttype.model.field.CheckboxField",
        Name           = name,
        Variable       = variable,
        DataType       = "TEXT",
        FieldTypeLabel = "Checkbox",
        Indexed        = true,
        Searchable     = true,
        Values         = "true|true",
    };

    // ------------------------------------------------------------------
    // Utility
    // ------------------------------------------------------------------

    private static string ToCamelCase(string name)
    {
        string[] parts = Regex.Split(name.Trim(), @"[\s_\-]+");
        if (parts.Length == 0) return "genericModule";

        string first = parts[0];
        if (string.IsNullOrEmpty(first)) return "genericModule";

        string result = char.ToLowerInvariant(first[0]) + (first.Length > 1 ? first[1..] : string.Empty);
        foreach (string part in parts[1..])
        {
            if (!string.IsNullOrEmpty(part))
                result += char.ToUpperInvariant(part[0]) + (part.Length > 1 ? part[1..] : string.Empty);
        }
        return string.IsNullOrEmpty(result) ? "genericModule" : result;
    }
}
