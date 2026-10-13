using Autodesk.ModelDerivative.Model;
using ApsMdExtractor.Services;

/// <summary>
/// Thin wrapper around ModelDerivativeClient.
/// The SDK does not let us plug in our own HttpClient handler, so counting happens here.
/// Each method makes exactly one SDK call, which is exactly one Model Derivative HTTP request.
/// </summary>
public class CountedModelDerivative
{
    private readonly APS _aps;
    private readonly MdCallCounter _counter;
    private readonly string _token;
    private readonly Region _region;

    public CountedModelDerivative(APS aps, MdCallCounter counter, string accessToken, Region region)
    {
        _aps = aps;
        _counter = counter;
        _token = accessToken;
        _region = region;
    }

    public Task<Manifest> GetManifestAsync(string urn) =>
        _counter.Track("GET manifest",
            () => _aps.ModelDerivative.GetManifestAsync(urn, region: _region, accessToken: _token),
            m => (200, $"status={m.Status}, progress={m.Progress}"));

    public Task<ModelViews> GetModelViewsAsync(string urn) =>
        _counter.Track("GET metadata",
            () => _aps.ModelDerivative.GetModelViewsAsync(urn, region: _region, accessToken: _token));

    public Task<ObjectTree> GetObjectTreeAsync(string urn, string guid) =>
        _counter.Track("GET metadata/{guid} (object tree)",
            () => _aps.ModelDerivative.GetObjectTreeAsync(urn, guid, region: _region, accessToken: _token),
            t => t.IsProcessing ? (202, "processing") : (200, null));

    public Task<Properties> GetAllPropertiesAsync(string urn, string guid) =>
        _counter.Track("GET metadata/{guid}/properties",
            () => _aps.ModelDerivative.GetAllPropertiesAsync(urn, guid, region: _region, accessToken: _token),
            p => p.IsProcessing ? (202, "processing") : (200, null));

    public Task<SpecificProperties> FetchSpecificPropertiesAsync(string urn, string guid, SpecificPropertiesPayload payload) =>
        _counter.Track("POST metadata/{guid}/properties:query",
            () => _aps.ModelDerivative.FetchSpecificPropertiesAsync(urn, guid, payload, region: _region, accessToken: _token),
            p => p.IsProcessing ? (202, "processing") : (200, null));

    public Task<DerivativeDownload> GetDerivativeUrlAsync(string urn, string derivativeUrn) =>
        _counter.Track("GET manifest/{derivativeUrn}/signedcookies",
            () => _aps.ModelDerivative.GetDerivativeUrlAsync(derivativeUrn, urn, region: _region, accessToken: _token));
}
