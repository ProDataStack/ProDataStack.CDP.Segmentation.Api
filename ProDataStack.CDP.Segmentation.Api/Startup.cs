using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ProDataStack.Chassis;

namespace ProDataStack.CDP.Segmentation.Api
{
    public class Startup : ProDataStack.Chassis.StartupBase
    {
        public override void ConfigureComponentServices(IServiceCollection services)
        {
            services.AddChassisAuthentication(Configuration);
            services.AddAMQP(Configuration);

            services.AddHealthChecks();

            services.AddApplicationInsightsTelemetry(options =>
            {
                options.ConnectionString = Configuration["AzureMonitor:ConnectionString"];
            });

            var env = Configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";
            var otelBuilder = services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation();
                    metrics.AddHttpClientInstrumentation();
                    metrics.AddRuntimeInstrumentation();
                })
                .WithTracing(tracing =>
                {
                    tracing.AddAspNetCoreInstrumentation();
                    tracing.AddHttpClientInstrumentation();
                });

            if (!env.Equals("Development", StringComparison.OrdinalIgnoreCase))
            {
                otelBuilder.UseAzureMonitor();
            }
        }

        public override void ConfigureComponent(IApplicationBuilder app)
        {
        }
    }
}
