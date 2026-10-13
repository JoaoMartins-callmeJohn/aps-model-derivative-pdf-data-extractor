using System.Globalization;
using System.Text.Json;
using Autodesk.ModelDerivative;
using Autodesk.ModelDerivative.Model;
using Newtonsoft.Json.Linq;

namespace ApsMdExtractor.Services;

/// <summary>A string found by the Viewer's Autodesk.StringExtractor extension. Used only when no PDF derivative exists.</summary>
public class ClientString
{
    public string String { get; set; } = "";
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
    public double Angle { get; set; }
    public double StringHeight { get; set; }
}

public class ExtractRequest
{
    public string Urn { get; set; } = "";
    public string ViewGuid { get; set; } = "";
    public string? ViewName { get; set; }
    public string? FileName { get; set; }
    public string? Region { get; set; }
    public List<ClientString>? ClientStrings { get; set; }
}

public class ExtractionResult
{
    public required string FilePath { get; init; }
    public required string DownloadName { get; init; }
    public required string TextSource { get; init; }
    public int ObjectCount { get; init; }
    public int TextRunCount { get; init; }
    public List<string> Warnings { get; init; } = [];
}

/// <summary>An expected failure, returned to the client with an HTTP status.</summary>
public class ExtractionException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Builds one .sdb file for one 2D view.
/// All Model Derivative calls go through CountedModelDerivative so each one is counted.
/// </summary>
public class ExtractionService
{
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(2);
    private const int MaxPolls = 30;
    private const int QueryBatchSize = 1000;

    private readonly APS _aps;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ExtractionService> _logger;

    public ExtractionService(APS aps, IHttpClientFactory httpFactory, ILogger<ExtractionService> logger)
    {
        _aps = aps;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public MdCallCounter Counter { get; } = new();

    public async Task<ExtractionResult> ExtractAsync(ExtractRequest req, Tokens tokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Urn) || string.IsNullOrWhiteSpace(req.ViewGuid))
        {
            throw new ExtractionException(400, "Both urn and viewGuid are required.");
        }
        var region = Enum.TryParse<Region>(req.Region, ignoreCase: true, out var r) ? r : Region.US;
        var md = new CountedModelDerivative(_aps, Counter, tokens.InternalToken, region);
        var warnings = new List<string>();

        // 1. Manifest: check translation state and find the view's PDF derivative, if any.
        var manifest = await md.GetManifestAsync(req.Urn);
        if (manifest.Status is "inprogress" or "pending")
        {
            throw new ExtractionException(409, $"The file is still being translated ({manifest.Progress}). Try again later.");
        }
        var pdf = FindPdfResource(manifest, req.ViewGuid);

        // 2. Metadata: make sure the view exists and is 2D.
        var views = await md.GetModelViewsAsync(req.Urn);
        var metadataViews = views.Data?.Metadata ?? [];
        var view = metadataViews.FirstOrDefault(v => string.Equals(v.Guid, req.ViewGuid, StringComparison.OrdinalIgnoreCase))
            ?? metadataViews.FirstOrDefault(v => v.Role == Role._2d && string.Equals(v.Name, req.ViewName, StringComparison.OrdinalIgnoreCase));
        if (view is null)
        {
            throw new ExtractionException(404, $"View {req.ViewGuid} was not found in the model metadata.");
        }
        if (view.Role != Role._2d)
        {
            throw new ExtractionException(400, $"View '{view.Name}' is not a 2D view.");
        }
        var guid = view.Guid;

        // 3. Object tree (hierarchy and names).
        var tree = await PollAsync(() => md.GetObjectTreeAsync(req.Urn, guid), t => t.IsProcessing, "object tree", ct);
        var objects = new Dictionary<long, DbObject>();
        foreach (var node in tree.Data?.Objects ?? [])
        {
            AddTreeNode(node, null, objects);
        }

        // 4. Properties: all at once, or in pages when the payload is too large.
        var collection = await GetPropertiesAsync(md, req.Urn, guid, objects.Keys.ToList(), warnings, ct);
        foreach (var item in collection)
        {
            if (item.ObjectId is not decimal oid)
            {
                continue;
            }
            var id = (long)oid;
            if (!objects.TryGetValue(id, out var obj))
            {
                objects[id] = obj = new DbObject { Id = id };
            }
            obj.ExternalId = item.ExternalId;
            obj.Name ??= item.Name;
            AddProperties(obj, item.Properties);
        }

        // 5. Text: PDF derivative first, then strings sent by the Viewer.
        var (textRuns, textSource, pdfPage) = await GetTextAsync(md, req, pdf, warnings, ct);
        AddTextObjects(objects, textRuns, textSource);

        // 6. Write the .sdb. Every Model Derivative call is done, so the count is final.
        var metadata = new Dictionary<string, string>
        {
            ["urn"] = req.Urn,
            ["viewGuid"] = req.ViewGuid,
            ["metadataGuid"] = guid,
            ["viewName"] = view.Name ?? req.ViewName ?? "",
            ["fileName"] = req.FileName ?? "",
            ["region"] = region.ToString(),
            ["extractedAt"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["textSource"] = textSource,
            ["textRunCount"] = textRuns.Count.ToString(CultureInfo.InvariantCulture),
            ["pdfDerivativeUrn"] = pdf.Resource?.Urn ?? "",
            ["pdfPage"] = pdfPage?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["modelDerivativeCallCount"] = Counter.Total.ToString(CultureInfo.InvariantCulture),
            ["modelDerivativeCalls"] = JsonSerializer.Serialize(Counter.Calls),
            ["warnings"] = string.Join(" | ", warnings),
        };
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sdb");
        PropertyDbWriter.Write(path, objects.Values, metadata);

        _logger.LogInformation("Extracted {File} / {View}: {Objects} objects, {Text} text runs ({Source}), {Calls} Model Derivative calls",
            req.FileName, view.Name, objects.Count, textRuns.Count, textSource, Counter.Total);

        return new ExtractionResult
        {
            FilePath = path,
            DownloadName = MakeFileName(req.FileName, view.Name ?? req.ViewName),
            TextSource = textSource,
            ObjectCount = objects.Count,
            TextRunCount = textRuns.Count,
            Warnings = warnings,
        };
    }

    // ---------- Model Derivative steps ----------

    private static async Task<T> PollAsync<T>(Func<Task<T>> call, Func<T, bool> isProcessing, string what, CancellationToken ct)
    {
        for (var i = 0; i < MaxPolls; i++)
        {
            var result = await call();
            if (!isProcessing(result))
            {
                return result;
            }
            await Task.Delay(PollDelay, ct);
        }
        throw new ExtractionException(504, $"Model Derivative is still preparing the {what}. Try again in a minute.");
    }

    private async Task<List<PropertiesDataCollection>> GetPropertiesAsync(CountedModelDerivative md, string urn, string guid,
        List<long> objectIds, List<string> warnings, CancellationToken ct)
    {
        try
        {
            var props = await PollAsync(() => md.GetAllPropertiesAsync(urn, guid), p => p.IsProcessing, "properties", ct);
            return props.Data?.Collection ?? [];
        }
        catch (ModelDerivativeApiException ex) when (MdCallCounter.StatusOf(ex) == 413)
        {
            warnings.Add("The properties payload was too large, so properties were fetched in pages with properties:query.");
        }

        var result = new List<PropertiesDataCollection>();
        foreach (var batch in objectIds.Order().Chunk(QueryBatchSize))
        {
            var payload = new SpecificPropertiesPayload
            {
                Query = new MatchId { In = ["objectid", .. batch.Cast<object>()] },
                Pagination = new SpecificPropertiesPayloadPagination { Offset = 0, Limit = QueryBatchSize },
            };
            var page = await PollAsync(() => md.FetchSpecificPropertiesAsync(urn, guid, payload), p => p.IsProcessing, "properties", ct);
            result.AddRange(page.Data?.Collection ?? []);
        }
        return result;
    }

    private async Task<(List<TextRun> runs, string source, int? page)> GetTextAsync(CountedModelDerivative md, ExtractRequest req,
        PdfMatch pdf, List<string> warnings, CancellationToken ct)
    {
        if (pdf.Resource?.Urn is string derivativeUrn)
        {
            try
            {
                var download = await md.GetDerivativeUrlAsync(req.Urn, derivativeUrn);
                // The file itself comes from the CDN with a signed URL. That download is not a Model Derivative call.
                var bytes = await _httpFactory.CreateClient().GetByteArrayAsync(download.Url, ct);
                var runs = PdfTextExtractor.Extract(bytes, pdf.Page, out var pageCount);
                if (pdf.Page is null && pageCount > 1)
                {
                    warnings.Add($"The PDF derivative has {pageCount} pages and the page for this view is unknown, so text from all pages was included.");
                }
                return (runs, PdfTextExtractor.Source, pdf.Page);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not read the PDF derivative {Urn}", derivativeUrn);
                warnings.Add($"The PDF derivative could not be read ({ex.GetType().Name}: {ex.Message}).");
            }
        }
        else
        {
            warnings.Add("This view has no PDF derivative.");
        }

        if (req.ClientStrings is { Count: > 0 } strings)
        {
            const string source = "viewer-string-extractor";
            var runs = strings
                .Where(s => !string.IsNullOrWhiteSpace(s.String))
                .Select(s => new TextRun(1, s.String.Trim(), s.MinX, s.MinY, s.MaxX - s.MinX, s.MaxY - s.MinY, s.Angle, s.StringHeight, source))
                .ToList();
            return (runs, source, null);
        }

        warnings.Add("No text was extracted: there is no PDF derivative and the Viewer sent no strings.");
        return ([], "none", null);
    }

    // ---------- Manifest helpers ----------

    private record PdfMatch(ManifestResources? Resource, int? Page);

    private static IEnumerable<ManifestResources> Walk(IEnumerable<ManifestResources>? nodes)
    {
        foreach (var node in nodes ?? [])
        {
            yield return node;
            foreach (var child in Walk(node.Children))
            {
                yield return child;
            }
        }
    }

    private static bool IsPdf(ManifestResources r) =>
        !string.IsNullOrEmpty(r.Urn) && (
            (r.Mime?.Contains("pdf", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (r.Role?.Contains("pdf", StringComparison.OrdinalIgnoreCase) ?? false) ||
            r.Urn.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds the PDF derivative for a 2D view.
    /// RVT 2022+ sheets and DWG layouts translated with "2dviews: pdf" have one PDF under each view node.
    /// If there is no per-view PDF but the manifest has exactly one PDF (for example a PDF upload),
    /// the page number is the position of the view among the 2D views.
    /// </summary>
    private static PdfMatch FindPdfResource(Manifest manifest, string viewGuid)
    {
        var roots = (manifest.Derivatives ?? []).SelectMany(d => d.Children ?? []).ToList();
        var all = Walk(roots).ToList();

        var viewNode = all.FirstOrDefault(n => string.Equals(n.Guid, viewGuid, StringComparison.OrdinalIgnoreCase));
        if (viewNode is not null)
        {
            var own = Walk(viewNode.Children).FirstOrDefault(IsPdf);
            if (own is not null)
            {
                return new PdfMatch(own, null);
            }
        }

        var pdfs = all.Where(IsPdf).GroupBy(p => p.Urn).Select(g => g.First()).ToList();
        if (pdfs.Count == 1)
        {
            var views2d = all.Where(n => n.Type == "geometry" && n.Role == "2d").ToList();
            var index = viewNode is null ? -1 : views2d.IndexOf(viewNode);
            return new PdfMatch(pdfs[0], index >= 0 ? index + 1 : null);
        }
        return new PdfMatch(null, null);
    }

    // ---------- Building objects ----------

    private static void AddTreeNode(ObjectTreeDataObjects node, long? parent, Dictionary<long, DbObject> objects)
    {
        if (node.ObjectId is not decimal oid)
        {
            return;
        }
        var id = (long)oid;
        objects[id] = new DbObject { Id = id, Name = node.Name, ParentId = parent };
        foreach (var child in node.Objects ?? [])
        {
            AddTreeNode(child, id, objects);
        }
    }

    /// <summary>
    /// Model Derivative returns properties as { "Category": { "Name": value } }.
    /// A value can be a list when a name repeats.
    /// </summary>
    private static void AddProperties(DbObject obj, Dictionary<string, object>? props)
    {
        foreach (var (key, value) in props ?? [])
        {
            if (value is JObject category)
            {
                foreach (var prop in category.Properties())
                {
                    AddValue(obj, key, prop.Name, prop.Value);
                }
            }
            else
            {
                AddValue(obj, "", key, value is JToken token ? token : JToken.FromObject(value));
            }
        }
    }

    private static void AddValue(DbObject obj, string category, string name, JToken token)
    {
        switch (token)
        {
            case JArray array:
                foreach (var item in array)
                {
                    AddValue(obj, category, name, item);
                }
                break;
            case JValue { Type: JTokenType.Null }:
                break;
            case JValue v:
                obj.Props.Add((category, name, v.Value));
                break;
            default:
                obj.Props.Add((category, name, token.ToString(Newtonsoft.Json.Formatting.None)));
                break;
        }
    }

    /// <summary>Adds a "Text" node under the model root, with one child object per text run.</summary>
    private static void AddTextObjects(Dictionary<long, DbObject> objects, List<TextRun> runs, string source)
    {
        var nextId = (objects.Count == 0 ? 0 : objects.Keys.Max()) + 1;
        var rootId = objects.Values.Where(o => o.ParentId is null).Select(o => (long?)o.Id).Min();
        var textRoot = new DbObject { Id = nextId++, Name = "Text", ParentId = rootId, ExternalId = "text" };
        textRoot.Props.Add(("Text", "Source", source));
        textRoot.Props.Add(("Text", "Count", runs.Count));
        objects[textRoot.Id] = textRoot;

        var i = 0;
        foreach (var run in runs)
        {
            var obj = new DbObject { Id = nextId++, Name = run.Content, ParentId = textRoot.Id, ExternalId = $"text-{++i}" };
            obj.Props.Add(("Text", "Content", run.Content));
            obj.Props.Add(("Text", "Page", run.Page));
            obj.Props.Add(("Text", "X", Math.Round(run.X, 4)));
            obj.Props.Add(("Text", "Y", Math.Round(run.Y, 4)));
            obj.Props.Add(("Text", "Width", Math.Round(run.Width, 4)));
            obj.Props.Add(("Text", "Height", Math.Round(run.Height, 4)));
            obj.Props.Add(("Text", "Rotation", run.Rotation));
            obj.Props.Add(("Text", "FontSize", run.FontSize));
            obj.Props.Add(("Text", "Source", run.Source));
            objects[obj.Id] = obj;
        }
    }

    private static string MakeFileName(string? fileName, string? viewName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName ?? "model");
        var name = string.IsNullOrWhiteSpace(viewName) ? baseName : $"{baseName}_{viewName}";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return (safe.Length > 150 ? safe[..150] : safe) + ".sdb";
    }
}
