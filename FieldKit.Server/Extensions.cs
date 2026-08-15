using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // FieldKit's own signals (W13 slice 1). One name, because a meter name is what an
                    // exporter subscribes to: nine of them would be nine subscriptions to keep in
                    // step, and the tenth would go missing quietly. See `Telemetry`.
                    .AddMeter(FieldKit.BuildingBlocks.Telemetry.MeterName);
            })
            .WithTracing(tracing =>
            {
                // FieldKit's own spans (W13 slice 2), beside the host's. Same name as the meter,
                // because they are two subscriptions to one product — see `Telemetry`.
                tracing.AddSource(FieldKit.BuildingBlocks.Telemetry.ActivitySourceName)
                    .AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Maps <c>/health</c> and <c>/alive</c> — in every environment, and terse outside development
    /// (W13 slice 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The template mapped these only in Development</b>, with a link explaining the security
    /// implications, and the reasoning is sound: the default response body names every check, how
    /// long it took and what exception it threw — a description of a service's dependencies, offered
    /// to anyone who can reach the port.
    /// </para>
    /// <para>
    /// <b>What it did not survive is a deployment.</b> W15 puts this on Container Apps, which probes
    /// an endpoint to decide whether an instance is alive and whether it may take traffic. Left as it
    /// was, both probes would have found nothing — and the failure would have arrived as a revision
    /// that never goes healthy, days after the code that caused it.
    /// </para>
    /// <para>
    /// So both are mapped and the <i>body</i> is what changes. Outside Development the response is a
    /// single word — <c>Healthy</c>, <c>Degraded</c>, <c>Unhealthy</c> — which is everything a probe
    /// reads and nothing an attacker can use. The status code is identical either way, so a platform
    /// behaves the same in both. In Development the full report stays, because that is where somebody
    /// is looking at it with their own eyes.
    /// </para>
    /// </remarks>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        var writer = HealthResponseWriter(app.Environment.IsDevelopment());

        // Readiness: every check, including the dependencies FieldKit adds (`HealthChecks`).
        app.MapHealthChecks(HealthEndpointPath, new HealthCheckOptions { ResponseWriter = writer });

        // Liveness: only checks tagged `live` — this process answering, never a dependency. A
        // liveness probe that fails on a database asks the platform to restart a working service.
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live"),
            ResponseWriter = writer,
        });

        return app;
    }

    /// <summary>
    /// How a health response is written: a per-check report, or the status alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The terse form is the framework's own default, which is worth knowing before designing
    /// around it.</b> ASP.NET's default writer emits one word — <c>Healthy</c> — and nothing else. So
    /// the exposure the template's comment warns about is the endpoint <i>existing</i>, not the body
    /// leaking: mapping these outside Development says "this port answers health probes" and no more
    /// than that.
    /// </para>
    /// <para>
    /// Which inverts the work. Production needed no redaction; <b>Development needed detail it did
    /// not have</b> — "Unhealthy" with no further comment is a poor morning when three checks could
    /// each be the reason. So the writer below exists for the environment where somebody is reading
    /// it with their own eyes, and the other environments keep the default.
    /// </para>
    /// <para>
    /// Public so the choice can be asserted directly. Standing a second host up in Production to read
    /// one string would spend forty seconds of Testcontainers on a sentence, and would be testing the
    /// host's environment plumbing rather than this decision.
    /// </para>
    /// </remarks>
    public static Func<HttpContext, HealthReport, Task> HealthResponseWriter(bool detailed) => detailed
        ? WriteReportAsync
        : static (context, report) =>
        {
            context.Response.ContentType = "text/plain";
            return context.Response.WriteAsync(report.Status.ToString());
        };

    /// <summary>The whole report, for a person.</summary>
    /// <remarks>
    /// The exception's <i>message</i> rather than the exception: a stack trace here is the framework's
    /// own frames nine times out of ten, and the sentence — "password authentication failed" — is the
    /// part that says what to do next.
    /// </remarks>
    private static Task WriteReportAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            entries = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    durationMs = entry.Value.Duration.TotalMilliseconds,
                    error = entry.Value.Exception?.Message,
                }),
        });
    }
}
