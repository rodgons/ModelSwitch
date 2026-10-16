using System.Globalization;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelSwitch.SharedKernel.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ModelSwitch.Telemetry;

/// <summary>
/// Traces, metrics and logs for a host (observability.md). Export is best-effort: with no <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> nothing
/// is exported, and with one the OTLP exporter batches through a bounded queue (2 048 items, the SDK default) that drops when full.
/// A backend that is down at startup or later never fails a request or the host (observability.md section 5).
/// </summary>
public static class TelemetryHostingExtensions
{
    public const string OtlpEndpointSetting = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string ServiceNameSetting = "OTEL_SERVICE_NAME";
    public const string SampleRatioSetting = "Telemetry:TraceSampleRatio";

    /// <summary>Framework sources and meters that are enabled besides <see cref="GatewayTelemetry.Name"/>.</summary>
    internal static readonly string[] TraceSources = [GatewayTelemetry.Name, "Microsoft.AspNetCore", "System.Net.Http", "Npgsql"];

    internal static readonly string[] Meters =
    [
        GatewayTelemetry.Name,
        $"{GatewayTelemetry.Name}.*",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "System.Net.Http",
        "System.Runtime",
        "Npgsql",
    ];

    /// <summary>
    /// JSON console logs with scopes and trace ids, redaction of <c>[SecretData]</c> and <c>[ContentData]</c>, and the OpenTelemetry
    /// SDK for all three signals. <paramref name="defaultServiceName"/> applies unless <c>OTEL_SERVICE_NAME</c> is set.
    /// </summary>
    public static IHostApplicationBuilder AddModelSwitchTelemetry(this IHostApplicationBuilder builder, string defaultServiceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultServiceName);

        var configuration = builder.Configuration;
        var serviceName = configuration[ServiceNameSetting] is { Length: > 0 } configured ? configured : defaultServiceName;
        var ratio = SampleRatio(configuration);
        var exportOtlp = !string.IsNullOrWhiteSpace(configuration[OtlpEndpointSetting]);

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        });

        // Every line inside a request carries the trace id; RequestId and TenantId come from logging scopes.
        builder.Logging.Configure(options => options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        AddLogRedaction(builder.Logging, builder.Services);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)))
                .AddSource(TraceSources))
            .WithMetrics(metrics => metrics.AddMeter(Meters))
            .WithLogging(configureBuilder: null, configureOptions: logging =>
            {
                logging.IncludeScopes = true;
                logging.IncludeFormattedMessage = true;
            });

        if (exportOtlp)
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>Erases every classified value from log lines: <c>[SecretData]</c>, <c>[ContentData]</c>, and anything else classified.</summary>
    internal static void AddLogRedaction(ILoggingBuilder logging, IServiceCollection services)
    {
        logging.EnableRedaction();
        services.AddRedaction(redaction =>
        {
            redaction.SetRedactor<ErasingRedactor>(DataClassifications.Secret, DataClassifications.Content);
            redaction.SetFallbackRedactor<ErasingRedactor>();
        });
    }

    internal static double SampleRatio(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[SampleRatioSetting];
        return raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) && ratio is >= 0 and <= 1 ? ratio : 1.0;
    }
}
