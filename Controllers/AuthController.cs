using System.Globalization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly APS _aps;

    public AuthController(APS aps)
    {
        _aps = aps;
    }

    private static readonly CookieOptions CookieOpts = new() { HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true };

    private static void WriteTokens(HttpResponse response, Tokens tokens)
    {
        response.Cookies.Append("public_token", tokens.PublicToken, CookieOpts);
        response.Cookies.Append("internal_token", tokens.InternalToken, CookieOpts);
        response.Cookies.Append("refresh_token", tokens.RefreshToken, CookieOpts);
        response.Cookies.Append("expires_at", tokens.ExpiresAt.ToString("o", CultureInfo.InvariantCulture), CookieOpts);
    }

    public static async Task<Tokens?> PrepareTokens(HttpRequest request, HttpResponse response, APS aps)
    {
        if (!request.Cookies.ContainsKey("internal_token"))
        {
            return null;
        }
        var tokens = new Tokens
        {
            PublicToken = request.Cookies["public_token"] ?? "",
            InternalToken = request.Cookies["internal_token"] ?? "",
            RefreshToken = request.Cookies["refresh_token"] ?? "",
            ExpiresAt = DateTime.TryParse(request.Cookies["expires_at"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var exp) ? exp : DateTime.MinValue
        };
        if (tokens.ExpiresAt < DateTime.UtcNow)
        {
            tokens = await aps.RefreshTokens(tokens);
            WriteTokens(response, tokens);
        }
        return tokens;
    }

    [HttpGet("login")]
    public ActionResult Login()
    {
        return Redirect(_aps.GetAuthorizationURL());
    }

    [HttpGet("logout")]
    public ActionResult Logout()
    {
        Response.Cookies.Delete("public_token");
        Response.Cookies.Delete("internal_token");
        Response.Cookies.Delete("refresh_token");
        Response.Cookies.Delete("expires_at");
        return Redirect("/");
    }

    [HttpGet("callback")]
    public async Task<ActionResult> Callback(string code)
    {
        var tokens = await _aps.GenerateTokens(code);
        WriteTokens(Response, tokens);
        return Redirect("/");
    }

    [HttpGet("profile")]
    public async Task<ActionResult> GetProfile()
    {
        var tokens = await PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        var profile = await _aps.GetUserProfile(tokens);
        return Ok(new { name = profile.Name });
    }

    [HttpGet("token")]
    public async Task<ActionResult> GetPublicToken()
    {
        var tokens = await PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        var expiresIn = Math.Floor((tokens.ExpiresAt - DateTime.UtcNow).TotalSeconds);
        return Ok(new { access_token = tokens.PublicToken, expires_in = expiresIn });
    }
}
