# DnnToDotCMS

A C# (.NET 8) command-line tool that converts **DNN (DotNetNuke)** site exports into **DotCMS** content type definitions.

## Overview

DNN organises content in *modules* placed on pages. DotCMS organises content in *Content Types* with structured fields. This tool reads a DNN export and outputs a JSON array of DotCMS content type definitions ready to be imported via the DotCMS REST API.

## Features

- Parses three DNN export formats:
  - **Official site export folder** — the folder produced by DNN's built-in Export/Import feature (e.g. `2026-03-29_01-49-26/`). The tool reads `export_packages.zip` inside the folder, extracts every `Module_*.resources` archive, and parses the `.dnn` manifest inside each one.
  - **Package manifests** (`.dnn` files) — the standard DNN module-installation format
  - **IPortable module-content exports** — produced when individual modules are exported from a DNN page
- Maps 14 common DNN module types to DotCMS content types out of the box (see table below)
- De-duplicates: multiple DNN modules of the same type produce a single content type definition
- Falls back to a generic two-field content type for unrecognised module types
- Outputs pretty-printed or compact JSON

## Supported Module Mappings

| DNN Module | DotCMS Content Type | Fields |
|---|---|---|
| HTML / Text/HTML | HTMLContent | Title, Body |
| Announcements | Announcement | Title, Description, Publish Date, Expire Date, URL, Image |
| Events / DNNEvents | Event | Event Name, Start Date, End Date, Location, Description, All Day, Category, Image |
| FAQs | FAQ | Question, Answer, Category, Created Date |
| Forms | FormSubmission | Form Name, Submitter Name, Email, Message, Submitted At |
| Blog | BlogPost | Title, Body, Author, Publish Date, Tags, Status, Featured Image |
| Documents / Document Library | Document | Title, File, Description, Category, Created Date, Owner |
| Links | Link | Title, URL, Description, Target, Created Date |
| Contacts | Contact | Full Name, Email, Phone, Company, Job Title, Notes, Photo |
| News Feed | NewsItem | Headline, Body, Source, Source URL, Published Date, Image |
| Gallery | GalleryItem | Title, Caption, Image, Album, Date Taken |
| Feedback | Feedback | Name, Email, Subject, Message, Submitted At |
| *(unknown)* | *(module name)* | Title, Content |

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later

## Quick Start

```bash
git clone https://github.com/tony-adm/DnnToDotCMS.git
cd DnnToDotCMS
dotnet run --project DnnToDotCms -- example/2026-03-29_01-49-26/export.json --pretty
```

> **Note:** The executable project lives in the `DnnToDotCms/` subfolder.
> Always include `--project DnnToDotCms` when running from the repo root, or
> `cd` into the subfolder first and use `../` to reach the example files:
>
> ```bash
> cd DnnToDotCms
> dotnet run -- ../example/2026-03-29_01-49-26/export.json --pretty
> ```

## Build

```bash
# From the repo root — builds the entire solution
dotnet build
```

## Run

All commands below are run from the **repo root**. The `--project DnnToDotCms`
flag tells the .NET SDK which project to run (the executable is in the
`DnnToDotCms/` subfolder, not in the root).

```bash
# Use export.json from a DNN official site-export folder (recommended)
dotnet run --project DnnToDotCms -- example/2026-03-29_01-49-26/export.json --pretty

# Or pass the folder directly
dotnet run --project DnnToDotCms -- example/2026-03-29_01-49-26 --pretty

# Use a single .dnn package manifest
dotnet run --project DnnToDotCms -- samples/sample-site-export.dnn --pretty

# Write output to a file
dotnet run --project DnnToDotCms -- example/2026-03-29_01-49-26/export.json --output content-types.json

# Help
dotnet run --project DnnToDotCms -- --help
```

Or build a self-contained executable first:

```bash
dotnet publish DnnToDotCms -c Release -o ./publish
./publish/DnnToDotCms example/2026-03-29_01-49-26/export.json --output output/content-types.json
```

## Input Formats

### 1. DNN Official Site-Export — `export.json` (recommended)

DNN's built-in **Export / Import** wizard produces a timestamped folder (e.g.
`2026-03-29_01-49-26`) containing `export.json` and several ZIP archives.
Pass the **`export.json` path** to the converter:

```bash
dotnet run --project DnnToDotCms -- example/2026-03-29_01-49-26/export.json --pretty
```

You can also pass the **folder path** directly and the tool will find
`export_packages.zip` automatically:

```bash
dotnet run --project DnnToDotCms -- example/2026-03-29_01-49-26 --pretty
```

The tool opens `export_packages.zip`, iterates over every `Module_*.resources`
entry (each is itself a ZIP), extracts the `.dnn` manifest inside each one, and
parses it. `Skin_*.resources` and other non-module entries are silently skipped.

Typical folder layout produced by DNN Export:

```
2026-03-29_01-49-26/
  export.json            ← export metadata (portal name, date, summary)  ← pass this
  export_db.zip          ← LiteDB site database (not used by this tool)
  export_files.zip       ← uploaded file assets
  export_packages.zip    ← installed module/skin packages  ← read by this tool
  export_templates.zip   ← page templates
  export_themes.zip      ← installed themes
```

### 2. DNN Package Manifest (`.dnn` file)

A standard DNN module-installation manifest with a
`<dotnetnuke type="Package">` root element:

```bash
dotnet run --project DnnToDotCms -- samples/sample-site-export.dnn --pretty
```

### 3. IPortable Module-Content Export (`.xml` file)

Produced when a single module is exported from a DNN page:

```bash
dotnet run --project DnnToDotCms -- samples/sample-html-module.xml --pretty
```

## Output Format

The output is a JSON array of DotCMS content type objects, each compatible with the DotCMS REST API:

```
POST /api/v1/contenttype
Content-Type: application/json

[ { ... content type definition ... } ]
```

Example output for an HTML module:

```json
[
  {
    "clazz": "com.dotcms.contenttype.model.type.SimpleContentType",
    "name": "HTMLContent",
    "variable": "htmlContent",
    "description": "Converted from DNN HTML module",
    "icon": "fa fa-code",
    "fields": [
      {
        "clazz": "com.dotcms.contenttype.model.field.TextField",
        "name": "Title",
        "variable": "title",
        "dataType": "TEXT",
        "fieldTypeLabel": "Text",
        "required": true,
        "listed": true
      },
      {
        "clazz": "com.dotcms.contenttype.model.field.WysiwygField",
        "name": "Body",
        "variable": "body",
        "dataType": "LONG_TEXT",
        "fieldTypeLabel": "WYSIWYG",
        "required": true
      }
    ]
  }
]
```

## Project Structure

```
DnnToDotCms/
  Program.cs                  CLI entry point
  Models/
    DnnModels.cs              DNN data models (DnnModule, DnnModuleDefinition, …)
    DotCmsModels.cs           DotCMS data models (DotCmsContentType, DotCmsField)
  Parser/
    DnnXmlParser.cs           Parses DNN exports into DnnModule objects (folder, .dnn, IPortable)
  Mappings/
    ModuleMappings.cs         Maps DNN module names to DotCMS content type definitions
  Converter/
    DnnConverter.cs           Converts DnnModule objects to DotCmsContentType objects

DnnToDotCms.Tests/
  DnnXmlParserTests.cs        Tests for the DNN XML parser (including export-folder format)
  ModuleMappingsTests.cs      Tests for the module-type mappings
  DnnConverterTests.cs        Tests for the conversion logic

example/
  2026-03-29_01-49-26/        Real DNN official site-export folder ("My Website")
    export.json               Export metadata
    export_packages.zip       Module packages (read by this tool)
    export_db.zip             LiteDB site database
    export_files.zip          Uploaded file assets
    export_templates.zip      Page templates
    export_themes.zip         Installed themes

samples/
  sample-site-export.dnn      Multi-module DNN package manifest
  sample-html-module.xml      Single IPortable HTML module export
```

## Tests

```bash
dotnet test
```

All 50 unit tests cover the parser (including the export-folder format), mappings, and converter.

