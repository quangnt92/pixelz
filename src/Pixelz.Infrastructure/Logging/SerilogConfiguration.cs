using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Pixelz.Infrastructure.Logging;

public static class SerilogConfiguration
{
    /// <summary>
    /// Configures Serilog with:
    ///   - Console sink: human-readable in dev, compact JSON in production
    ///   - Rolling file sink: compact JSON, daily rotation, 30-day retention
    ///
    /// Log files are written to the path defined in appsettings: Serilog:LogDirectory
    /// Default path: logs/pixelz-.json
    ///
    /// Log levels by environment:
    ///   Development  — Debug+ for app code, Information for Microsoft/System
    ///   Production   — Information+ for app code, Warning for Microsoft/System
    /// </summary>
    public static IHostBuilder UsePixelzSerilog(this IHostBuilder builder)
    {
        return builder.UseSerilog((context, services, config) =>
        {
            var cfg = context.Configuration;
            var env = context.HostingEnvironment;

            var logDir = cfg["Serilog:LogDirectory"] ?? "logs";
            var logPath = Path.Combine(logDir, "pixelz-.json");

            config
                // ── Minimum levels ─────────────────────────────────────────
                .MinimumLevel.Is(env.IsDevelopment() ? LogEventLevel.Debug : LogEventLevel.Information)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", env.IsDevelopment() ? LogEventLevel.Information : LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)

                // ── Enrichers ──────────────────────────────────────────────
                .Enrich.FromLogContext()
                .Enrich.WithEnvironmentName()
                .Enrich.WithMachineName()
                .Enrich.WithProcessId()
                .Enrich.WithThreadId()

                // ── Console sink ───────────────────────────────────────────
                // Development : plain text template for easy reading
                // Production  : compact JSON for log aggregators
                .WriteTo.Console(
                    outputTemplate: env.IsDevelopment()
                        ? "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}\n  {Message:lj}\n{Exception}"
                        : "{@l}: {@m}\n{@x}",
                    restrictedToMinimumLevel: LogEventLevel.Debug)

                // ── File sink (rolling daily, compact JSON) ────────────────
                .WriteTo.File(
                    formatter: new CompactJsonFormatter(),
                    path: logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    fileSizeLimitBytes: 100 * 1024 * 1024, // 100 MB per file
                    rollOnFileSizeLimit: true,
                    shared: false,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    restrictedToMinimumLevel: LogEventLevel.Information);
        });
    }
}