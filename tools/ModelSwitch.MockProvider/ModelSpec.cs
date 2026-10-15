using System.Globalization;

namespace ModelSwitch.MockProvider;

/// <summary>
/// The behaviour encoded in a mock model id: <c>mock-echo</c> echoes the last user message,
/// <c>mock-fixed?latency=300&amp;tokens=50</c> returns a fixed-size output after a fixed latency. Any other name behaves like <c>mock-fixed</c>
/// so tests can use <c>mock-a</c>, <c>mock-b</c> as distinct models.
/// </summary>
internal sealed record ModelSpec(string Name, bool Echo, TimeSpan Latency, int Tokens)
{
    public const int DefaultTokens = 20;

    public static bool TryParse(string? model, out ModelSpec? spec, out string? error)
    {
        spec = null;
        error = null;
        if (string.IsNullOrWhiteSpace(model))
        {
            error = "The model is required.";
            return false;
        }

        var queryStart = model.IndexOf('?', StringComparison.Ordinal);
        var name = queryStart < 0 ? model : model[..queryStart];
        var latency = TimeSpan.Zero;
        var tokens = DefaultTokens;

        if (queryStart >= 0)
        {
            foreach (var pair in model[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    error = $"Invalid model parameter '{pair}'.";
                    return false;
                }

                switch (parts[0])
                {
                    case "latency":
                        latency = TimeSpan.FromMilliseconds(number);
                        break;
                    case "tokens":
                        tokens = number;
                        break;
                    default:
                        error = $"Unknown model parameter '{parts[0]}'.";
                        return false;
                }
            }
        }

        spec = new ModelSpec(name, string.Equals(name, "mock-echo", StringComparison.Ordinal), latency, tokens);
        return true;
    }
}

/// <summary>Usage overrides from <c>X-Mock-Usage</c>, e.g. <c>cached=900</c> or <c>input=100;output=7</c>.</summary>
internal sealed record UsageOverride(int? Input, int? Output, int? Cached)
{
    public static UsageOverride None { get; } = new(null, null, null);

    public static bool TryParse(string? text, out UsageOverride? result, out string? error)
    {
        result = None;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        int? input = null, output = null, cached = null;
        foreach (var pair in text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                error = $"Invalid usage directive '{pair}'.";
                result = null;
                return false;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "input":
                    input = number;
                    break;
                case "output":
                    output = number;
                    break;
                case "cached":
                    cached = number;
                    break;
                default:
                    error = $"Unknown usage directive '{parts[0]}'.";
                    result = null;
                    return false;
            }
        }

        result = new UsageOverride(input, output, cached);
        return true;
    }
}
