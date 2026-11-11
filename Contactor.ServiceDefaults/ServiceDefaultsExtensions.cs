using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Contactor.ServiceDefaults;

/// <summary>
/// Shared setup for every process: OpenTelemetry (logs, metrics, traces) exported to the Aspire dashboard when it
/// runs, health checks and resilient HTTP clients.
/// </summary>
public static class ServiceDefaultsExtensions
{
    private const string HealthPath = "/health";
    private const string AlivePath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureOpenTelemetry();
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        return builder;
    }

    /// <summary>Maps /health and /alive for the Aspire dashboard. Only in development: they can leak details.</summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks(HealthPath);
            app.MapHealthChecks(AlivePath, new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") });
        }

        return app;
    }

    private static void ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
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
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthPath) &&
                            !context.Request.Path.StartsWithSegments(AlivePath);
                    })
                    .AddHttpClientInstrumentation();
            });

        // Only exports when Aspire (or another collector) is configured, so production sends nothing anywhere.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
    }
}
