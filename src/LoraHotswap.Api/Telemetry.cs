using System.Diagnostics;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LoraHotswap.Api;

/// <summary>
/// The system's single OpenTelemetry trace source (AD-10) and its wiring. Spans are
/// always observable on the console with no collector running, and additionally
/// exported over OTLP when an endpoint is configured.
/// </summary>
public static class LoraHotswapTelemetry
{
    /// <summary>The one ActivitySource name for the whole system (Spine: Tracing convention).</summary>
    public const string ActivitySourceName = "LoraHotswap";

    /// <summary>The configuration section for exporter settings.</summary>
    public const string SectionName = "OpenTelemetry";

    /// <summary>The shared source every span is started from.</summary>
    public static ActivitySource Source { get; } = new(ActivitySourceName);

    /// <summary>
    /// Register tracing for <see cref="ActivitySourceName"/>.
    /// <list type="bullet">
    ///   <item><c>OpenTelemetry:ConsoleExporter</c> (default <see langword="true"/>) -- console export.</item>
    ///   <item><c>OpenTelemetry:OtlpEndpoint</c>, or the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
    ///   variable -- enables OTLP export to that endpoint.</item>
    ///   <item><c>OpenTelemetry:OtlpProtocol</c> -- <c>grpc</c> or <c>http/protobuf</c>; unset defers to
    ///   <c>OTEL_EXPORTER_OTLP_PROTOCOL</c>, then the exporter default (<c>grpc</c>). With
    ///   <c>http/protobuf</c>, a base endpoint gets <c>/v1/traces</c> appended.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddLoraHotswapTracing(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(SectionName);
        bool console = section.GetValue("ConsoleExporter", defaultValue: true);
        string? otlpEndpoint = section["OtlpEndpoint"];
        bool otlp = !string.IsNullOrWhiteSpace(otlpEndpoint)
                    || !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        // Only an explicit OtlpProtocol overrides the exporter's own default, which honours
        // the standard OTEL_EXPORTER_OTLP_PROTOCOL variable.
        OtlpExportProtocol? protocol = section["OtlpProtocol"] switch
        {
            null or "" => null,
            "grpc" => OtlpExportProtocol.Grpc,
            "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
            var other => throw new InvalidOperationException(
                $"{SectionName}: OtlpProtocol '{other}' is not supported. Expected 'grpc' or 'http/protobuf'."),
        };

        Uri? endpoint = null;
        if (!string.IsNullOrWhiteSpace(otlpEndpoint)
            && (!Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException(
                $"{SectionName}: OtlpEndpoint '{otlpEndpoint}' must be an absolute http:// or https:// URL.");
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("lora-hotswap-server"))
            .WithTracing(tracing =>
            {
                // GOTCHA: ASP.NET Core wraps each request in an activity whenever hosting
                // diagnostics are enabled (e.g. logging is on). That activity is not recorded,
                // and the default parent-based sampler would drop every child span -- including
                // adapter.verification. Sampling every LoraHotswap span regardless of its parent
                // keeps them, without exporting HTTP server spans that carry no adapter.id.
                tracing.SetSampler(new AlwaysOnSampler()).AddSource(ActivitySourceName);

                if (console)
                {
                    tracing.AddConsoleExporter();
                }

                if (otlp)
                {
                    tracing.AddOtlpExporter(options =>
                    {
                        if (protocol is { } p)
                        {
                            options.Protocol = p;
                        }

                        if (endpoint is not null)
                        {
                            // An endpoint set in code is used verbatim; unlike the environment
                            // variable, http/protobuf does not get the signal path appended.
                            options.Endpoint = options.Protocol == OtlpExportProtocol.HttpProtobuf
                                               && endpoint.AbsolutePath == "/"
                                ? new Uri(endpoint, "v1/traces")
                                : endpoint;
                        }
                    });
                }
            });

        return services;
    }
}
