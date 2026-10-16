using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ModelSwitch.Api;

/// <summary>
/// The web JSON defaults also read numbers written as strings, so the generated schema says <c>number | string</c> for every
/// number. Responses always write numbers, and the dashboard's client is generated from this document, so it describes them as
/// numbers only.
/// </summary>
internal static class OpenApiNumbers
{
    public static Task NumbersOnly(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.Type is { } type && (type.HasFlag(JsonSchemaType.Integer) || type.HasFlag(JsonSchemaType.Number)) && type.HasFlag(JsonSchemaType.String))
        {
            schema.Type = type & ~JsonSchemaType.String;
            schema.Pattern = null;
        }

        return Task.CompletedTask;
    }
}
