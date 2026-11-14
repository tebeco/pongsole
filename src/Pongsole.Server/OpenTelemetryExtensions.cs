using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Pongsole.Server;

internal static class OpenTelemetryExtensions
{
    internal static void AddOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options => options.Filter = context =>
                    context.Request.Path != "/health" && context.Request.Path != "/alive"
                    && !context.Request.Path.StartsWithSegments("/rtc")
                    && context.Request.Path != DiscordSignIn.CallbackPath)
                .AddHttpClientInstrumentation(options => options.FilterHttpRequestMessage =
                    request => request.RequestUri?.Host is not (CloudflareTurnCredentialProvider.ApiHost or DiscordSignIn.ApiHost)));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }
    }
}
