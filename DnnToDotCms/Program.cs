using System.Text.Json;
using DnnToDotCms.Converter;
using DnnToDotCms.Models;
using DnnToDotCms.Parser;

// ---------------------------------------------------------------------------
// DNN → DotCMS Converter  —  CLI entry point
// ---------------------------------------------------------------------------
//
// Usage:
//   DnnToDotCms <input> [--output <output.json>] [--pretty]
//   DnnToDotCms --help
//
// Arguments:
//   <input>                  Path to a DNN export folder, the export.json manifest
//                            inside that folder, or a DNN XML file (.dnn / IPortable)
//
// Options:
//   --output <path>          Write output to a file instead of stdout
//   --pretty                 Indent the JSON output (default when writing to file)
//   --help, -h               Show this help and exit
// ---------------------------------------------------------------------------

if (args.Length == 0 || args.Any(a => a is "--help" or "-h"))
{
    PrintUsage();
    return 0;
}

// Parse arguments
string? inputPath  = null;
string? outputPath = null;
bool    pretty     = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output" when i + 1 < args.Length:
            outputPath = args[++i];
            break;
        case "--pretty":
            pretty = true;
            break;
        default:
            if (!args[i].StartsWith("--"))
                inputPath = args[i];
            else
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                PrintUsage();
                return 1;
            }
            break;
    }
}

if (string.IsNullOrWhiteSpace(inputPath))
{
    Console.Error.WriteLine("Error: No input path specified.");
    PrintUsage();
    return 1;
}

bool isDirectory      = Directory.Exists(inputPath);
bool isFile           = !isDirectory && File.Exists(inputPath);
bool isExportManifest = isFile &&
    Path.GetFileName(inputPath).Equals("export.json", StringComparison.OrdinalIgnoreCase);

if (!isDirectory && !isFile)
{
    Console.Error.WriteLine($"Error: Input path not found: {inputPath}");
    return 1;
}

// When writing to a file, default to pretty-printed output.
if (outputPath is not null)
    pretty = true;

try
{
    // Parse DNN export (folder, export.json manifest, or XML file)
    IReadOnlyList<DnnModule> modules = isDirectory
        ? DnnXmlParser.ParseExportFolder(inputPath)
        : isExportManifest
            ? DnnXmlParser.ParseExportJson(inputPath)
            : DnnXmlParser.ParseFile(inputPath);

    if (modules.Count == 0)
    {
        Console.Error.WriteLine("Warning: No DNN modules found in the input file.");
        return 0;
    }

    // Convert to DotCMS content types
    IReadOnlyList<DotCmsContentType> contentTypes = DnnConverter.ConvertAll(modules);

    // Serialise
    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented          = pretty,
        PropertyNamingPolicy   = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    string json = JsonSerializer.Serialize(contentTypes, jsonOptions);

    // Output
    if (outputPath is not null)
    {
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"Converted {modules.Count} module(s) to {contentTypes.Count} " +
                          $"content type(s). Output written to: {outputPath}");
    }
    else
    {
        Console.WriteLine(json);
    }

    return 0;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Error parsing DNN XML: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Unexpected error: {ex.Message}");
    return 1;
}

// ---------------------------------------------------------------------------

static void PrintUsage()
{
    Console.WriteLine("""
        DNN to DotCMS Converter v1.0.0
        Converts DNN (DotNetNuke) module exports to DotCMS content type definitions.

        Usage:
          DnnToDotCms <input> [--output <output.json>] [--pretty]
          DnnToDotCms --help

        Arguments:
          <input>             Path to a DNN official site-export folder, or the
                              export.json manifest inside that folder, or a DNN
                              XML file (.dnn or IPortable export)

        Options:
          --output <path>     Write JSON output to a file (also enables pretty-print)
          --pretty            Indent the JSON output on stdout
          --help, -h          Show this help message

        Supported DNN module types:
          HTML / Text-HTML, Announcements, Events, FAQs, Forms, Blog,
          Documents, Links, Contacts, News Feed, Gallery, Feedback.
          Unknown module types produce a generic HTMLContent content type.

        Output:
          A JSON array of DotCMS content type definitions suitable for import
          via the DotCMS REST API:  POST /api/v1/contenttype

        Examples:
          DnnToDotCms example/2026-03-29_01-49-26/export.json
          DnnToDotCms example/2026-03-29_01-49-26/export.json --output content-types.json
          DnnToDotCms example/2026-03-29_01-49-26
          DnnToDotCms site-export.dnn
          DnnToDotCms module-export.xml --pretty
        """);
}

