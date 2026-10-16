using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Evaluation.Domain.Datasets;

/// <summary>One validated Case of an upload, its JSON values in canonical form (<see cref="CanonicalJson"/>).</summary>
internal sealed record ParsedCase(string ExternalId, int Ordinal, string InputJson, string ExpectedJson, string? MetadataJson);

/// <summary>A whole valid upload: its Cases in file order, the hash of their canonical form and the scoring schema.</summary>
internal sealed record ParsedDataset(IReadOnlyList<ParsedCase> Cases, string ContentSha256, string ScoringSchemaJson);

/// <summary>
/// Parses and validates a Dataset Version upload (evaluation.md section 1). The upload is UTF-8 JSONL, one Case per line; blank
/// lines are ignored. It is accepted or rejected as a whole: any problem fails it with <c>validation_failed</c> and errors keyed
/// <c>lines[N]</c> or <c>lines[N].field</c>, where N is the 1-based line number in the file, plus <c>body</c> for problems with the
/// upload as a whole. Pure: no I/O, and Case content never reaches a log or an error message.
/// </summary>
internal static class DatasetJsonl
{
    /// <summary>Largest accepted upload, in bytes (10 MiB).</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    public const int MaxCases = 5_000;

    /// <summary>At most this many invalid lines are reported, so a wholly wrong file gets a bounded answer.</summary>
    public const int MaxReportedLines = 100;

    public const int MaxIdLength = 256;

    public const int MaxLabelLength = 256;

    public const string BodyKey = "body";

    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> _fields = new(StringComparer.Ordinal) { "id", "input", "expected", "metadata" };

    public static Result<ParsedDataset> Parse(ReadOnlySpan<byte> utf8, TaskKind kind)
    {
        string text;
        try
        {
            text = _strictUtf8.GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            return Invalid(new Dictionary<string, string[]> { [BodyKey] = ["The upload must be UTF-8 text."] });
        }

        // A byte order mark is tolerated, as editors on some platforms write one.
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var lines = text.Split('\n');
        var caseLines = lines.Count(line => !string.IsNullOrWhiteSpace(line));
        if (caseLines == 0)
        {
            return Invalid(new Dictionary<string, string[]> { [BodyKey] = ["The upload contains no Cases: send one JSON object per line."] });
        }

        if (caseLines > MaxCases)
        {
            return Invalid(new Dictionary<string, string[]> { [BodyKey] = [$"The upload has {caseLines} Cases; a Dataset Version holds at most {MaxCases}."] });
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var invalidLines = 0;
        var cases = new List<ParsedCase>(caseLines);
        var canonicalLines = new List<string>(caseLines);
        var lineOfId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var lineNumber = index + 1;
            var lineErrors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var parsed = ParseLine(line.TrimEnd('\r'), lineNumber, kind, cases.Count, lineOfId, lineErrors);
            if (lineErrors.Count > 0)
            {
                invalidLines++;
                if (invalidLines <= MaxReportedLines)
                {
                    foreach (var (key, messages) in lineErrors)
                    {
                        errors[key] = [.. messages];
                    }
                }

                continue;
            }

            cases.Add(parsed!.Value.Case);
            canonicalLines.Add(parsed.Value.Canonical);
        }

        if (invalidLines > 0)
        {
            if (invalidLines > MaxReportedLines)
            {
                errors[BodyKey] = [$"{invalidLines} lines are invalid; only the first {MaxReportedLines} are listed."];
            }

            return Invalid(errors);
        }

        return new ParsedDataset(cases, Sha256Hex(string.Join('\n', canonicalLines)), ScoringSchema(kind, cases));
    }

    /// <summary>Lower-case hex SHA-256 of the UTF-8 bytes of <paramref name="text"/>.</summary>
    public static string Sha256Hex(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static (ParsedCase Case, string Canonical)? ParseLine(
        string line,
        int lineNumber,
        TaskKind kind,
        int ordinal,
        Dictionary<string, int> lineOfId,
        Dictionary<string, List<string>> errors)
    {
        var prefix = $"lines[{lineNumber.ToString(CultureInfo.InvariantCulture)}]";
        void Add(string? field, string message)
        {
            var key = field is null ? prefix : $"{prefix}.{field}";
            if (!errors.TryGetValue(key, out var list))
            {
                errors[key] = list = [];
            }

            list.Add(message);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line, CanonicalJson.DocumentOptions);
        }
        catch (JsonException)
        {
            // The parser's message is not copied: it is about the customer's text, which is never echoed or logged.
            Add(null, "The line is not valid JSON, or repeats a field name.");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Add(null, "Each line must be a JSON object with id, input and expected.");
                return null;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!_fields.Contains(property.Name))
                {
                    Add(Truncate(property.Name), "Unknown field: a Case has only id, input, expected and metadata.");
                }
            }

            var id = ValidateId(root, lineNumber, lineOfId, Add);
            if (!root.TryGetProperty("input", out var input))
            {
                Add("input", "The input is required.");
            }
            else if (input.ValueKind != JsonValueKind.Object)
            {
                Add("input", "The input must be a JSON object whose keys are the Prompt Version's template variables.");
            }

            ValidateExpected(root, kind, Add);

            var hasMetadata = root.TryGetProperty("metadata", out var metadata);
            if (hasMetadata && metadata.ValueKind != JsonValueKind.Object)
            {
                Add("metadata", "The metadata, when present, must be a JSON object.");
            }

            if (errors.Count > 0)
            {
                return null;
            }

            try
            {
                var expected = root.GetProperty("expected");
                var parsed = new ParsedCase(
                    id!,
                    ordinal,
                    CanonicalJson.Serialize(input),
                    CanonicalJson.Serialize(expected),
                    hasMetadata ? CanonicalJson.Serialize(metadata) : null);
                return (parsed, CanonicalJson.Serialize(root));
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                Add(null, "The line contains a string that is not valid Unicode.");
                return null;
            }
        }
    }

    private static string? ValidateId(JsonElement root, int lineNumber, Dictionary<string, int> lineOfId, Action<string?, string> add)
    {
        if (!root.TryGetProperty("id", out var idElement))
        {
            add("id", "The id is required.");
            return null;
        }

        if (idElement.ValueKind != JsonValueKind.String)
        {
            add("id", "The id must be a string.");
            return null;
        }

        string? id;
        try
        {
            id = idElement.GetString();
        }
        catch (InvalidOperationException)
        {
            add("id", "The id is not valid Unicode.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            add("id", "The id must not be empty.");
            return null;
        }

        if (id.Length > MaxIdLength)
        {
            add("id", $"The id must be at most {MaxIdLength} characters.");
            return null;
        }

        if (lineOfId.TryGetValue(id, out var firstLine))
        {
            add("id", $"The id is already used on line {firstLine.ToString(CultureInfo.InvariantCulture)}; ids are unique within a Dataset Version.");
            return null;
        }

        lineOfId[id] = lineNumber;
        return id;
    }

    private static void ValidateExpected(JsonElement root, TaskKind kind, Action<string?, string> add)
    {
        if (!root.TryGetProperty("expected", out var expected))
        {
            add("expected", "The expected result is required.");
            return;
        }

        switch (kind)
        {
            case TaskKind.Extraction:
                if (expected.ValueKind != JsonValueKind.Object)
                {
                    add("expected", "An extraction Case expects a JSON object of fields.");
                }
                else if (!expected.EnumerateObject().Any())
                {
                    add("expected", "An extraction Case expects at least one field.");
                }

                break;
            case TaskKind.Classification:
                if (expected.ValueKind != JsonValueKind.String || !TryGetString(expected, out var label))
                {
                    add("expected", "A classification Case expects its label as a string.");
                }
                else if (string.IsNullOrWhiteSpace(label))
                {
                    add("expected", "The label must not be empty.");
                }
                else if (label.Length > MaxLabelLength)
                {
                    add("expected", $"The label must be at most {MaxLabelLength} characters.");
                }
                else if (label.Trim().Length != label.Length)
                {
                    add("expected", "The label must not start or end with whitespace: outputs are matched to labels after trimming.");
                }

                break;
            case TaskKind.FreeText:
                if (expected.ValueKind != JsonValueKind.String || !TryGetString(expected, out var reference))
                {
                    add("expected", "A free-text Case expects its reference answer as a string.");
                }
                else if (string.IsNullOrWhiteSpace(reference))
                {
                    add("expected", "The reference answer must not be empty.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static bool TryGetString(JsonElement element, out string value)
    {
        try
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }
        catch (InvalidOperationException)
        {
            value = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// <c>{"taskKind":…}</c> plus, for classification, <c>labels</c> (the distinct expected labels) and, for extraction, <c>fields</c>
    /// (every field name any Case expects), both sorted in ordinal order.
    /// </summary>
    private static string ScoringSchema(TaskKind kind, IReadOnlyList<ParsedCase> cases)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("taskKind", TaskKinds.ToWire(kind));
            switch (kind)
            {
                case TaskKind.Classification:
                    WriteSortedStrings(writer, "labels", cases.Select(c => JsonSerializer.Deserialize<string>(c.ExpectedJson)!));
                    break;
                case TaskKind.Extraction:
                    WriteSortedStrings(writer, "fields", cases.SelectMany(c =>
                    {
                        using var expected = JsonDocument.Parse(c.ExpectedJson);
                        return expected.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                    }));
                    break;
                case TaskKind.FreeText:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void WriteSortedStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    // An unknown field name is the customer's text; it is echoed only in the response, and bounded.
    private static string Truncate(string name) => name.Length <= 64 ? name : name[..64];

    private static Error Invalid(IReadOnlyDictionary<string, string[]> errors)
        => Error.Validation("The dataset upload is invalid.", errors);
}
