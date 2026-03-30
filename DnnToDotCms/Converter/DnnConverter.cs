using DnnToDotCms.Mappings;
using DnnToDotCms.Models;

namespace DnnToDotCms.Converter;

/// <summary>
/// Converts <see cref="DnnModule"/> objects into <see cref="DotCmsContentType"/>
/// objects using the mappings defined in <see cref="ModuleMappings"/>.
/// </summary>
public static class DnnConverter
{
    /// <summary>
    /// Convert a single <see cref="DnnModule"/> into a
    /// <see cref="DotCmsContentType"/>.
    /// </summary>
    /// <param name="module">A parsed DNN module.</param>
    /// <returns>
    /// A <see cref="DotCmsContentType"/> ready to be serialised and imported.
    /// </returns>
    public static DotCmsContentType Convert(DnnModule module)
    {
        DotCmsContentType contentType = ModuleMappings.GetContentType(module.ModuleName, module.FriendlyName);

        // Append module-level description when it adds context.
        if (!string.IsNullOrWhiteSpace(module.Description) &&
            !contentType.Description.Contains(module.Description, StringComparison.OrdinalIgnoreCase))
        {
            contentType.Description = string.IsNullOrWhiteSpace(contentType.Description)
                ? module.Description
                : $"{contentType.Description}. {module.Description}";
        }

        // DotCMS stores the content-type description in a VARCHAR(255) column.
        // Truncate here so that no bundle entry can exceed that limit.
        if (contentType.Description.Length > 255)
            contentType.Description = contentType.Description[..255];

        return contentType;
    }

    /// <summary>
    /// Convert a list of <see cref="DnnModule"/> objects, de-duplicating by
    /// DotCMS content-type <c>variable</c> so that multiple DNN modules of
    /// the same type produce only one content-type definition.
    /// </summary>
    /// <param name="modules">Parsed DNN modules.</param>
    /// <returns>
    /// Unique <see cref="DotCmsContentType"/> instances, one per distinct
    /// variable name.
    /// </returns>
    public static IReadOnlyList<DotCmsContentType> ConvertAll(IEnumerable<DnnModule> modules)
    {
        var seen    = new Dictionary<string, DotCmsContentType>(StringComparer.OrdinalIgnoreCase);
        foreach (DnnModule module in modules)
        {
            DotCmsContentType ct = Convert(module);
            seen.TryAdd(ct.Variable, ct);
        }
        return seen.Values.ToList();
    }
}
