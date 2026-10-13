using System.Text;
using System.Text.Json;
using ApsMdExtractor.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Receives the URN and view GUID from the browser and returns the .sdb file.
/// The server makes all Model Derivative calls. The response headers report how many were made.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ExtractController : ControllerBase
{
    private readonly APS _aps;
    private readonly ExtractionService _extraction;
    private readonly ILogger<ExtractController> _logger;

    public ExtractController(APS aps, ExtractionService extraction, ILogger<ExtractController> logger)
    {
        _aps = aps;
        _extraction = extraction;
        _logger = logger;
    }

    [HttpPost]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult> Extract([FromBody] ExtractRequest request, CancellationToken ct)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }

        var counter = _extraction.Counter;
        try
        {
            var result = await _extraction.ExtractAsync(request, tokens, ct);
            var bytes = await System.IO.File.ReadAllBytesAsync(result.FilePath, ct);
            System.IO.File.Delete(result.FilePath);

            Response.Headers["X-MD-Call-Count"] = counter.Total.ToString();
            Response.Headers["X-MD-Call-Breakdown"] = ToBase64Json(counter.Summary());
            Response.Headers["X-Text-Source"] = result.TextSource;
            Response.Headers["X-Object-Count"] = result.ObjectCount.ToString();
            Response.Headers["X-Text-Count"] = result.TextRunCount.ToString();
            Response.Headers["X-Warnings"] = ToBase64Json(result.Warnings);
            return File(bytes, "application/octet-stream", result.DownloadName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var status = ex switch
            {
                ExtractionException ee => ee.Status,
                // Pass through auth and not-found errors from Model Derivative; anything else is a 500.
                _ when MdCallCounter.StatusOf(ex) is 401 or 403 or 404 => MdCallCounter.StatusOf(ex),
                _ => 500
            };
            _logger.LogError(ex, "Extraction failed after {Calls} Model Derivative calls", counter.Total);
            // The count is still returned so the UI can show the calls spent on a failed extraction.
            return StatusCode(status, new
            {
                error = ex.Message,
                mdCallCount = counter.Total,
                mdCallBreakdown = counter.Summary(),
            });
        }
    }

    private static string ToBase64Json(object value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));
}
