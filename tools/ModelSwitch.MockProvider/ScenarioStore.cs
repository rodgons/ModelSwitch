using System.Collections.Concurrent;

namespace ModelSwitch.MockProvider;

/// <summary>
/// Holds faults set through <c>POST /__scenarios</c> (per mock model id) and the counters behind <c>times=N</c>.
/// Counters are keyed by <c>X-Mock-Scenario</c> (or the model id) plus the fault text, so independent tests do not interfere.
/// </summary>
internal sealed class ScenarioStore
{
    private readonly ConcurrentDictionary<string, string> _faultsByModel = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _counters = new(StringComparer.Ordinal);

    public void SetFault(string model, string faultText)
    {
        _faultsByModel[model] = faultText;
        ResetCounters(model);
    }

    public string? GetFault(string model) => _faultsByModel.TryGetValue(model, out var text) ? text : null;

    public void Clear()
    {
        _faultsByModel.Clear();
        _counters.Clear();
    }

    /// <summary>Counts one request against the fault and reports whether the fault applies to it.</summary>
    public bool ShouldApply(string scenarioKey, string faultText, FaultSpec spec)
    {
        if (spec.Times is not { } times)
        {
            return true;
        }

        var count = _counters.AddOrUpdate($"{scenarioKey}|{faultText}", 1, static (_, current) => current + 1);
        return count <= times;
    }

    private void ResetCounters(string scenarioKey)
    {
        var prefix = $"{scenarioKey}|";
        foreach (var key in _counters.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            _counters.TryRemove(key, out _);
        }
    }
}
