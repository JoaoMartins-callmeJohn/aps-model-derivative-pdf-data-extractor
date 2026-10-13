using Autodesk.Authentication.Model;
using Autodesk.ModelDerivative;

public class Tokens
{
    public string InternalToken = "";
    public string PublicToken = "";
    public string RefreshToken = "";
    public DateTime ExpiresAt;
}

public partial class APS
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _callbackUri;
    private readonly List<Scopes> InternalTokenScopes = [Scopes.DataRead, Scopes.ViewablesRead];
    private readonly List<Scopes> PublicTokenScopes = [Scopes.ViewablesRead];

    // One SDK client is enough. Every call passes its own access token and region.
    public ModelDerivativeClient ModelDerivative { get; } = new ModelDerivativeClient();

    public APS(string clientId, string clientSecret, string callbackUri)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _callbackUri = callbackUri;
    }
}
