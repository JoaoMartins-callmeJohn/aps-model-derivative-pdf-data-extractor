using System.Diagnostics;

namespace ApsMdExtractor.Services;

/// <summary>One Model Derivative HTTP request made by the server.</summary>
public record MdCall(int Seq, string Endpoint, int Status, long DurationMs, string? Note);

/// <summary>
/// Counts the Model Derivative requests made during one .sdb extraction.
/// A new instance is created for each extraction request.
/// </summary>
public class MdCallCounter
{
    private readonly List<MdCall> _calls = [];
    private readonly object _lock = new();

    public int Total { get { lock (_lock) return _calls.Count; } }

    public IReadOnlyList<MdCall> Calls { get { lock (_lock) return _calls.ToList(); } }

    /// <summary>
    /// Runs one SDK call, records it, and returns its result.
    /// The call is recorded even when it throws.
    /// </summary>
    public async Task<T> Track<T>(string endpoint, Func<Task<T>> call, Func<T, (int status, string? note)>? describe = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await call();
            var (status, note) = describe?.Invoke(result) ?? (200, null);
            Record(endpoint, status, sw.ElapsedMilliseconds, note);
            return result;
        }
        catch (Exception ex)
        {
            Record(endpoint, StatusOf(ex), sw.ElapsedMilliseconds, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>HTTP status of a failed SDK call. The SDK puts it on HttpResponseMessage, not on StatusCode.</summary>
    public static int StatusOf(Exception ex) => ex switch
    {
        Autodesk.ModelDerivative.ServiceApiException { HttpResponseMessage: { } resp } => (int)resp.StatusCode,
        HttpRequestException { StatusCode: { } code } => (int)code,
        _ => 0
    };

    private void Record(string endpoint, int status, long ms, string? note)
    {
        lock (_lock)
        {
            _calls.Add(new MdCall(_calls.Count + 1, endpoint, status, ms, note));
        }
    }

    /// <summary>Calls grouped by endpoint, for the UI breakdown.</summary>
    public IEnumerable<object> Summary() =>
        Calls.GroupBy(c => c.Endpoint).Select(g => new
        {
            endpoint = g.Key,
            count = g.Count(),
            statuses = string.Join(", ", g.GroupBy(c => c.Status).Select(s => $"{s.Key}x{s.Count()}")),
            totalMs = g.Sum(c => c.DurationMs)
        });
}
