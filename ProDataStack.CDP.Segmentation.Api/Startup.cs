using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ProDataStack.CDP.Segmentation.Api.Context;
using ProDataStack.Chassis;

namespace ProDataStack.CDP.Segmentation.Api
{
    public class Startup : StartupBase
    {
        public override void ConfigureComponentServices(IServiceCollection services)
        {
            var connectionString = Configuration.GetConnectionString("DatabaseConnection");

            services.AddDbContextFactory<SegmentationDbContext>(options =>
                options.UseSqlServer(connectionString));

            services.AddChassisAuthentication(Configuration);
            services.AddAMQP(Configuration);

            services.AddHealthChecks()
                .AddSqlServer(connectionString ?? string.Empty, name: "database");

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
