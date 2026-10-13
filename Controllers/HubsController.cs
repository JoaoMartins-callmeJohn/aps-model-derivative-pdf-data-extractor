using Autodesk.DataManagement.Model;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class HubsController : ControllerBase
{
    // Only these file types are listed in the tree.
    private static readonly string[] SupportedExtensions = [".rvt", ".dwg", ".pdf"];

    private readonly APS _aps;

    public HubsController(APS aps)
    {
        _aps = aps;
    }

    private static bool IsSupported(string? name) =>
        !string.IsNullOrEmpty(name) && SupportedExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    [HttpGet()]
    public async Task<ActionResult> ListHubs()
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        // The hub region is passed on to the Model Derivative calls (US, EMEA, AUS, ...).
        return Ok(
            from hub in await _aps.GetHubs(tokens)
            select new { id = hub.Id, name = hub.Attributes.Name, region = hub.Attributes.Region.ToString() }
        );
    }

    [HttpGet("{hub}/projects")]
    public async Task<ActionResult> ListProjects(string hub)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        return Ok(
            from project in await _aps.GetProjects(hub, tokens)
            select new { id = project.Id, name = project.Attributes.Name }
        );
    }

    [HttpGet("{hub}/projects/{project}/contents")]
    public async Task<ActionResult> ListItems(string hub, string project, [FromQuery] string? folder_id)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        if (string.IsNullOrEmpty(folder_id))
        {
            return Ok(
                from folder in await _aps.GetTopFolders(hub, project, tokens)
                select new { id = folder.Id, name = folder.Attributes.DisplayName, folder = true }
            );
        }
        var contents = await _aps.GetFolderContents(project, folder_id, tokens);
        var folders = contents.OfType<FolderData>()
            .Select(folder => new { id = folder.Id, name = folder.Attributes.DisplayName, folder = true });
        var items = contents.OfType<ItemData>()
            .Where(item => IsSupported(item.Attributes.DisplayName))
            .Select(item => new { id = item.Id, name = item.Attributes.DisplayName, folder = false });
        return Ok(folders.Concat(items));
    }

    [HttpGet("{hub}/projects/{project}/contents/{item}/versions")]
    public async Task<ActionResult> ListVersions(string hub, string project, string item)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        return Ok(
            from version in await _aps.GetVersions(project, item, tokens)
            select new { id = version.Id, name = $"v{version.Attributes.VersionNumber} - {version.Attributes.CreateTime}" }
        );
    }
}
