using System.Text.Json;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Evaluation.Domain.Evaluators;

/// <summary>
/// Reads a JSON object property by property and collects validation errors keyed by path (for example
/// <c>config.fields.email.tolerance</c>), so one 400 <c>validation_failed</c> reports every problem at once.
/// Properties the caller never reads are reported as unknown by <see cref="RejectUnknown"/>, which catches typos.
/// </summary>
internal sealed class JsonConfigReader
{
    private readonly Dictionary<string, List<string>> _errors;
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);
    private readonly JsonElement? _object;

    public JsonConfigReader(JsonElement? element, string path)
        : this(element, path, new Dictionary<string, List<string>>(StringComparer.Ordinal))
    {
    }

    private JsonConfigReader(JsonElement? element, string path, Dictionary<string, List<string>> errors)
    {
        _errors = errors;
        Path = path;
        if (element is { ValueKind: JsonValueKind.Object } obj)
        {
            _object = obj;
        }
        else if (element is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) })
        {
            AddError(path, "Must be an object.");
        }
    }

    public string Path { get; }

    public bool HasErrors => _errors.Count > 0;

    public string PathOf(string property) => Path.Length == 0 ? property : $"{Path}.{property}";

    public void AddError(string path, string message)
    {
        if (!_errors.TryGetValue(path, out var list))
        {
            list = [];
            _errors[path] = list;
        }

        list.Add(message);
    }

    /// <summary>The raw property, or null when it is absent or JSON null.</summary>
    public JsonElement? Property(string name)
    {
        _read.Add(name);
        if (_object is { } obj && obj.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
        {
            return value;
        }

        return null;
    }

    /// <summary>A reader over a nested object property, sharing this reader's error list.</summary>
    public JsonConfigReader Nested(JsonElement? element, string path) => new(element, path, _errors);

    public string? String(string name, bool required = false)
    {
        var value = Property(name);
        if (value is null)
        {
            if (required)
            {
                AddError(PathOf(name), "Is required.");
            }

            return null;
        }

        if (value.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.Value.GetString()))
        {
            AddError(PathOf(name), "Must be a non-empty string.");
            return null;
        }

        return value.Value.GetString();
    }

    public bool? Boolean(string name)
    {
        var value = Property(name);
        if (value is null)
        {
            return null;
        }

        if (value.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            AddError(PathOf(name), "Must be true or false.");
            return null;
        }

        return value.Value.GetBoolean();
    }

    public decimal? Decimal(string name, decimal min, decimal max)
    {
        var value = Property(name);
        if (value is null)
        {
            return null;
        }

        if (value.Value.ValueKind != JsonValueKind.Number || !value.Value.TryGetDecimal(out var number) || number < min || number > max)
        {
            AddError(PathOf(name), $"Must be a number from {min} to {max}.");
            return null;
        }

        return number;
    }

    /// <summary>The optional <c>passThreshold</c> every scoring-by-threshold Evaluator accepts: 0 to 1.</summary>
    public decimal PassThreshold(decimal defaultValue) => Decimal("passThreshold", 0m, 1m) ?? defaultValue;

    /// <summary>The names of the object's properties that were never read.</summary>
    public void RejectUnknown()
    {
        if (_object is not { } obj)
        {
            return;
        }

        foreach (var property in obj.EnumerateObject())
        {
            if (!_read.Contains(property.Name))
            {
                AddError(PathOf(property.Name), "Unknown property.");
            }
        }
    }

    public Error ToError(string message)
        => Error.Validation(message, _errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));

    /// <summary>The value built from a clean read, or the collected validation errors.</summary>
    public Result<T> Finish<T>(Func<T> build, string message)
    {
        ArgumentNullException.ThrowIfNull(build);
        RejectUnknown();
        return HasErrors ? ToError(message) : build();
    }
}
