# aps-model-derivative-pdf-data-extractor

![platforms](https://img.shields.io/badge/platform-windows%20%7C%20osx%20%7C%20linux-lightgray.svg)
[![.net](https://img.shields.io/badge/net-10.0-blue.svg)](https://dotnet.microsoft.com/)
[![License](http://img.shields.io/:license-MIT-blue.svg)](http://opensource.org/licenses/MIT)

This sample uses Autodesk Platform Services (APS) to export the properties **and text** of 2D views to a `.sdb` file. It works with PDF, DWG and RVT files stored in Autodesk Construction Cloud (ACC) or BIM 360. A request counter shows how many Model Derivative calls each file needed.

The UI is based on [aps-hubs-browser-dotnet](https://github.com/autodesk-platform-services/aps-hubs-browser-dotnet). The server uses the [APS SDK for .NET](https://github.com/autodesk-platform-services/aps-sdk-net).

## What it does

1. **Browse.** The tree shows your hubs, projects and folders. Only `.dwg`, `.rvt` and `.pdf` files are listed.
2. **View.** For RVT and DWG files, the Viewer shows 2D views only (sheets and layouts), chosen from the **2D view** dropdown. A PDF opens in the Viewer with one entry per page.
3. **Extract.** **Extract properties & text (.sdb)** sends the URN and view GUID of the current view to the server. The server makes every Model Derivative call and returns one `.sdb` file for that view.
4. **Count.** The **MD calls** badge shows how many Model Derivative requests produced the file. Click it for a breakdown by endpoint. The sidebar keeps a history of every extraction.

## How the server builds the .sdb

`POST /api/extract` runs these Model Derivative calls through the .NET SDK:

| Step | Endpoint | Purpose |
|---|---|---|
| 1 | `GET /designdata/{urn}/manifest` | Check the translation and find the view's PDF derivative |
| 2 | `GET /designdata/{urn}/metadata` | Check the view GUID exists and is 2D |
| 3 | `GET /designdata/{urn}/metadata/{guid}` | Object tree, polled while it returns 202 |
| 4 | `GET /designdata/{urn}/metadata/{guid}/properties` | All properties, polled while it returns 202 |
| 4b | `POST /designdata/{urn}/metadata/{guid}/properties:query` | Paged fallback when step 4 returns 413 |
| 5 | `GET /designdata/{urn}/manifest/{derivativeUrn}/signedcookies` | Signed URL of the view's PDF derivative |

A typical extraction makes 5 calls. Each 202 poll and each page of the fallback adds one. The PDF file itself downloads from the CDN with the signed URL. That download is not a Model Derivative call and is not counted.

### Where the text comes from

- **PDF derivative (preferred).** RVT 2022+ sheets, DWG layouts translated with PDF 2D views, and PDF uploads have a PDF derivative. The server reads its text lines with [PdfPig](https://github.com/UglyToad/PdfPig), including position, size, rotation and font size.
- **Viewer strings (fallback).** Legacy 2D views have no PDF derivative. For those, the server uses the strings the Viewer read with `Autodesk.StringExtractor`, as described in [Retrieving text from 2D views](https://aps.autodesk.com/blog/retrieving-text-2d-views). The browser always sends these strings, but the server only uses them when there is no PDF derivative.

The sample never starts translation jobs. It only uses derivatives that Autodesk Docs has already created.

### .sdb format

The file is SQLite and uses the same tables as the Viewer property database (`model.sdb`):

- `_objects_id`, `_objects_attr`, `_objects_val` and `_objects_eav` hold the objects and properties of the view.
- The hierarchy uses the standard `__parent__`, `__child__` and `__name__` attributes.
- A **Text** object sits under the model root. It has one child per text run, with attributes in the `Text` category: `Content`, `Page`, `X`, `Y`, `Width`, `Height`, `Rotation`, `FontSize` and `Source`.
- An extra `_metadata` table records the URN, view, text source, the Model Derivative call count and the full call log.

To list all text in a file:

```sql
SELECT v.value AS text
FROM _objects_eav e
JOIN _objects_attr a ON a.id = e.attribute_id
JOIN _objects_val v ON v.id = e.value_id
WHERE a.category = 'Text' AND a.name = 'Content';
```

To see how the file was made:

```sql
SELECT * FROM _metadata;
```

### How calls are counted

The SDK does not let you plug in a custom `HttpClient` handler. All Model Derivative calls therefore go through `CountedModelDerivative` in `Models/APS.Derivative.cs`. Each of its methods makes exactly one SDK call, which is one HTTP request. A fresh counter is created for each extraction. Failed calls and 202 responses are counted too. The count is also returned when an extraction fails.

The SDK's internal HTTP handler may retry throttled (429) or failed requests on its own. Those retries are invisible to the SDK caller and are not included in the count.

## Running locally

### Prerequisites

- An [APS application](https://aps.autodesk.com/myapps) with the callback URL `http://localhost:8080/api/auth/callback`.
- The app provisioned in your ACC or BIM 360 account as a custom integration.
- [.NET 10 SDK](https://dotnet.microsoft.com/download).

### Setup

Create `appsettings.Development.json` in the project root. It is gitignored.

```json
{
  "APS_CLIENT_ID": "<your client id>",
  "APS_CLIENT_SECRET": "<your client secret>",
  "APS_CALLBACK_URL": "http://localhost:8080/api/auth/callback"
}
```

Then run the app and open http://localhost:8080.

```bash
dotnet run
```

## Limitations

- The page of a multi-page PDF derivative is inferred from the order of the 2D views. If it cannot be determined, text from all pages is included and a warning is recorded.
- Coordinates from the PDF derivative are in PDF points. Coordinates from the Viewer fallback are in Viewer page units.
- The file must already be translated. If it is still processing, the server returns 409.

## License

This sample is licensed under the terms of the [MIT License](http://opensource.org/licenses/MIT). See [LICENSE](LICENSE) for details.
