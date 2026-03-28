# DnnToDotCMS

A C# (.NET 8) command-line tool that converts **DNN (DotNetNuke)** site exports into **DotCMS** content type definitions.

## Overview

DNN organises content in *modules* placed on pages. DotCMS organises content in *Content Types* with structured fields. This tool reads a DNN XML export and outputs a JSON array of DotCMS content type definitions ready to be imported via the DotCMS REST API.

## Features

- Parses two DNN XML formats:
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

## Build

```bash
git clone https://github.com/tony-adm/DnnToDotCMS.git
cd DnnToDotCMS
dotnet build
```

## Run

```bash
# Print JSON to stdout
dotnet run --project DnnToDotCms -- <input.dnn>

# Pretty-print to stdout
dotnet run --project DnnToDotCms -- <input.dnn> --pretty

# Write to a file (pretty-printed by default)
dotnet run --project DnnToDotCms -- <input.dnn> --output content-types.json

# Help
dotnet run --project DnnToDotCms -- --help
```

Or build a self-contained executable first:

```bash
dotnet publish DnnToDotCms -c Release -o ./publish
./publish/DnnToDotCms samples/sample-site-export.dnn --output output/content-types.json
```

## Sample Files

| File | Description |
|---|---|
| `samples/sample-site-export.dnn` | Multi-module package manifest (HTML, Events, FAQs, Blog, Documents) |
| `samples/sample-html-module.xml` | Single IPortable HTML module export |

```bash
dotnet run --project DnnToDotCms -- samples/sample-site-export.dnn --pretty
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
    DnnXmlParser.cs           Parses DNN XML into DnnModule objects
  Mappings/
    ModuleMappings.cs         Maps DNN module names to DotCMS content type definitions
  Converter/
    DnnConverter.cs           Converts DnnModule objects to DotCmsContentType objects

DnnToDotCms.Tests/
  DnnXmlParserTests.cs        Tests for the DNN XML parser
  ModuleMappingsTests.cs      Tests for the module-type mappings
  DnnConverterTests.cs        Tests for the conversion logic

samples/
  sample-site-export.dnn      Multi-module DNN package manifest
  sample-html-module.xml      Single IPortable HTML module export
```

## Tests

```bash
dotnet test
```

All 46 unit tests cover the parser, mappings, and converter.

