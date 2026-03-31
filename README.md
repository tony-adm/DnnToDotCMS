# DnnToDotCMS

A C# (.NET 8) command-line tool that converts **DNN (DotNetNuke)** site exports into a **DotCMS push-publish site bundle** (`.tar.gz`) that can be uploaded directly to DotCMS.

## Overview

DNN organises content in *modules* placed on pages. DotCMS organises content in *Content Types* with structured fields. This tool reads a DNN export and produces a DotCMS-compatible push-publish bundle containing:

- **Content types** — converted from DNN module definitions
- **Containers** — converted from DNN container ASCX templates (`.containers.container.xml`)
- **Templates/Layouts** — converted from DNN skin ASCX templates (`.template.template.xml`)
- **Static theme assets** — CSS, JS, images and fonts from the DNN skin

## Features

- Parses three DNN export formats:
  - **Official site export folder** — the folder produced by DNN's built-in Export/Import feature (e.g. `2026-03-29_01-49-26/`). The tool reads `export_packages.zip` inside the folder, extracts every `Module_*.resources` archive, and parses the `.dnn` manifest inside each one.
  - **Package manifests** (`.dnn` files) — the standard DNN module-installation format
  - **IPortable module-content exports** — produced when individual modules are exported from a DNN page
- Maps 14 common DNN module types to DotCMS content types out of the box (see table below)
- De-duplicates: multiple DNN modules of the same type produce a single content type definition
- Falls back to a generic two-field content type for unrecognised module types
- Converts DNN **container** ASCX templates to DotCMS container XML bundle entries
- Converts DNN **skin** ASCX templates to DotCMS template XML bundle entries
- Outputs a DotCMS push-publish bundle (`.tar.gz`) containing:
  - `working/{site}/{uuid}.contentType.json` — one file per converted content type
  - `live/{site}/{uuid}.containers.container.xml` — one file per DNN container (published)
  - `live/{site}/{uuid}.template.template.xml` — one file per DNN skin (published)
  - `live/{site}/1/{uuid}.content.xml` — one file per HTML module content item
  - `live/{site}/1/{uuid}.contentworkflow.xml` — workflow state for each content item
  - `manifest.csv` — bundle manifest listing all items
  - `ROOT/application/themes/{ThemeName}/…` — static theme assets (CSS, JS, images, fonts) placed in DotCMS's `/application/themes/` directory on import

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
dotnet run --project DnnToDotCms -- example/2026-03-29_01-49-26
# → produces site.tar.gz in the current directory
```

> **Note:** The executable project lives in the `DnnToDotCms/` subfolder.
> Always include `--project DnnToDotCms` when running from the repo root, or
> `cd` into the subfolder first and use `../` to reach the example files:
>
> ```bash
> cd DnnToDotCms
> dotnet run -- ../example/2026-03-29_01-49-26
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
# Use an export folder (also picks up export_themes.zip for theme assets)
dotnet run --project DnnToDotCms -- example/2026-03-29_01-49-26

# Or pass the export.json manifest directly
dotnet run --project DnnToDotCms -- example/2026-03-29_01-49-26/export.json

# Use a single .dnn package manifest
dotnet run --project DnnToDotCms -- samples/sample-site-export.dnn

# Write the bundle to a custom filename
dotnet run --project DnnToDotCms -- example/2026-03-29_01-49-26 --output my-site.tar.gz

# Help
dotnet run --project DnnToDotCms -- --help
```

Or build a self-contained executable first:

```bash
dotnet publish DnnToDotCms -c Release -o ./publish
./publish/DnnToDotCms example/2026-03-29_01-49-26 --output my-site.tar.gz
```

## Uploading to DotCMS

1. Run the tool to produce `site.tar.gz`.
2. In DotCMS, go to **Dev Tools → Push & Publish → Bundle Import** (or use the Push Publish REST endpoint).
3. Upload `site.tar.gz`. DotCMS will import all content types, containers, and templates listed in the bundle.

> **Theme assets note:** Static skin files (CSS, JS, images, fonts) are bundled
> under `ROOT/application/themes/{ThemeName}/` so that DotCMS automatically
> places them in the `/application/themes/` directory on the server when the
> bundle is imported.

## Input Formats

### 1. DNN Official Site-Export folder (recommended)

DNN's built-in **Export / Import** wizard produces a timestamped folder (e.g.
`2026-03-29_01-49-26`) containing `export.json` and several ZIP archives.
Pass the **folder path** (or the `export.json` path inside it) to the converter:

```bash
dotnet run --project DnnToDotCms -- example/2026-03-29_01-49-26
# or
dotnet run --project DnnToDotCms -- example/2026-03-29_01-49-26/export.json
```

When you pass the folder (or `export.json`), the tool automatically looks for
`export_themes.zip` alongside `export_packages.zip` and includes any static
theme assets in the output bundle.

The tool opens `export_packages.zip`, iterates over every `Module_*.resources`
entry (each is itself a ZIP), extracts the `.dnn` manifest inside each one, and
parses it. `Skin_*.resources` and other non-module entries are silently skipped.

Typical folder layout produced by DNN Export:

```
2026-03-29_01-49-26/
  export.json            ← export metadata (portal name, date, summary)
  export_db.zip          ← LiteDB site database (HTML module content extracted from here)
  export_files.zip       ← uploaded file assets
  export_packages.zip    ← installed module/skin packages  ← read by this tool
  export_templates.zip   ← page templates
  export_themes.zip      ← installed themes  ← static assets extracted into bundle
```

### 2. DNN Package Manifest (`.dnn` file)

A standard DNN module-installation manifest with a
`<dotnetnuke type="Package">` root element:

```bash
dotnet run --project DnnToDotCms -- samples/sample-site-export.dnn
```

### 3. IPortable Module-Content Export (`.xml` file)

Produced when a single module is exported from a DNN page:

```bash
dotnet run --project DnnToDotCms -- samples/sample-html-module.xml
```

## Output Format

The tool produces a **DotCMS push-publish bundle** (`.tar.gz`).

`site.tar.gz` is the native file format that DotCMS uses for its **Push & Publish** feature — the mechanism DotCMS provides for transferring site content between environments (e.g. from staging to production, or from an external source into a fresh DotCMS instance). Generating this file is the goal of the entire conversion: it packages everything DotCMS needs to reconstruct the migrated site in a single, self-contained archive.

When DotCMS imports the bundle it performs the following steps automatically:

1. **Reads `manifest.csv`** — the index that tells DotCMS which objects are in the bundle and in what order to process them.
2. **Creates content types** (from the `.contentType.json` files) — the structural schemas that describe how content is stored and displayed.
3. **Creates containers and templates** (from the `.container.xml` and `.template.xml` files) — the layout building blocks that render pages.
4. **Publishes content items** (from the `.content.xml` files) — the actual page content converted from DNN HTML modules.
5. **Writes static application files** (from entries under `ROOT/`) — CSS, JS, images, and fonts are extracted directly onto the server's file system so they are immediately served as static assets.

The result is that a single upload to **Dev Tools → Push & Publish → Bundle Import** migrates the full DNN site into DotCMS without any manual steps.

The bundle contains:

```
site.tar.gz
├── manifest.csv                                              ← bundle manifest
├── live/
│   └── {site}/
│       ├── {uuid}.containers.container.xml                   ← DNN container (published)
│       ├── {uuid}.template.template.xml                      ← DNN skin (published)
│       └── 1/
│           ├── {uuid}.content.xml                            ← HTML module content (published)
│           └── {uuid}.contentworkflow.xml                    ← workflow state for content
├── working/
│   └── {site}/
│       └── {uuid}.contentType.json                           ← one file per content type
└── ROOT/
    └── application/
        └── themes/
            └── {ThemeName}/                                      ← static skin assets (CSS/JS/images)
                ├── skin.css
                ├── Bootstrap/css/bootstrap.min.css
                └── …
```

### Container XML format

Each DNN container ASCX is converted to a DotCMS `ContainerWrapper` XML entry.
The ASCX markup is transformed:
- `<dnn:TITLE …/>` → `$dotContent.title` (Velocity variable)
- `<div id="ContentPane" runat="server">…</div>` → `$!{dotContent.body}` (Velocity variable)
- `runat="server"` attributes removed
- ASP.NET directive blocks (`<%@ … %>`) removed

### Template XML format

Each DNN skin ASCX is converted to a DotCMS `TemplateWrapper` XML entry.
The ASCX markup is transformed:
- `<dnn:LOGO …/>` → `<img src="/logo.png" alt="Logo" />`
- `<dnn:MENU …/>` → `<!-- Navigation -->`
- `<dnn:SEARCH …/>` → `<!-- Search -->`
- `<dnn:USER …/>` → `<!-- User Panel -->`
- `<dnn:LOGIN …/>` → `<!-- Login -->`
- `<dnn:COPYRIGHT …/>` → `<!-- Copyright -->`
- `<dnn:BREADCRUMB …/>` → `<!-- Breadcrumb -->`
- `<dnn:STYLES …/>`, `<dnn:jQuery …/>`, `<dnn:META …/>` removed (handled by DotCMS)
- All other structural HTML is preserved

### Content type JSON format

Each `contentType.json` file uses the DotCMS push-publish bundle format:

```json
{
  "contentType": {
    "clazz": "com.dotcms.contenttype.model.type.ImmutableSimpleContentType",
    "name": "HTMLContent",
    "id": "<uuid>",
    "variable": "htmlContent",
    "host": "SYSTEM_HOST",
    "folder": "SYSTEM_FOLDER"
  },
  "fields": [
    { "clazz": "com.dotcms.contenttype.model.field.ImmutableRowField", … },
    { "clazz": "com.dotcms.contenttype.model.field.ImmutableColumnField", … },
    { "clazz": "com.dotcms.contenttype.model.field.ImmutableTextField",
      "name": "Title", "variable": "title", "dbColumn": "text1", … },
    { "clazz": "com.dotcms.contenttype.model.field.ImmutableWysiwygField",
      "name": "Body",  "variable": "body",  "dbColumn": "text_area1", … }
  ],
  "workflowSchemaIds": ["d61a59e1-a49c-46f2-a929-db2b4bfa88b2"],
  "workflowSchemaNames": ["System Workflow"],
  "operation": "PUBLISH",
  "fieldVariables": [],
  "systemActionMappings": {}
}
```

## Project Structure

```
DnnToDotCms/
  Program.cs                  CLI entry point
  Models/
    DnnModels.cs              DNN data models (DnnModule, DnnModuleDefinition, …)
    DotCmsModels.cs           DotCMS data models (content type, field, and bundle-format models)
  Parser/
    DnnXmlParser.cs           Parses DNN exports into DnnModule objects (folder, .dnn, IPortable)
  Mappings/
    ModuleMappings.cs         Maps DNN module names to DotCMS content type definitions
  Converter/
    DnnConverter.cs           Converts DnnModule objects to DotCmsContentType objects
  Bundle/
    BundleWriter.cs           Writes a DotCMS push-publish bundle (.tar.gz)

DnnToDotCms.Tests/
  DnnXmlParserTests.cs        Tests for the DNN XML parser (including export-folder format)
  ModuleMappingsTests.cs      Tests for the module-type mappings
  DnnConverterTests.cs        Tests for the conversion logic
  BundleWriterTests.cs        Tests for the bundle writer

example/
  2026-03-29_01-49-26/        Real DNN official site-export folder ("My Website")
    export.json               Export metadata
    export_packages.zip       Module packages (read by this tool)
    export_db.zip             LiteDB site database
    export_files.zip          Uploaded file assets
    export_templates.zip      Page templates
    export_themes.zip         Installed themes (static assets bundled by this tool)

samples/
  sample-site-export.dnn      Multi-module DNN package manifest
  sample-html-module.xml      Single IPortable HTML module export
```

## Tests

```bash
dotnet test
```

All 71 unit tests cover the parser (including the export-folder format), mappings, converter, and bundle writer.

