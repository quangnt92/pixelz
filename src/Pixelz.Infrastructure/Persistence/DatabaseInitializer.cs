using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pixelz.Infrastructure.Persistence;

public static class DatabaseInitializer
{    
    public static async Task InitialiseAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PixelzDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<PixelzDbContext>>();

        try
        {
            logger.LogInformation("Database initialisation started. Provider={Provider}", db.Database.ProviderName);

            // Kiểm tra có pending migration không
            var pendingMigrations = (await db.Database.GetPendingMigrationsAsync()).ToList();
            var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync()).ToList();

            if (pendingMigrations.Count > 0)
            {
                logger.LogInformation("Applying {Count} pending migration(s): {Migrations}", pendingMigrations.Count, string.Join(", ", pendingMigrations));
                await db.Database.MigrateAsync();
                logger.LogInformation("Migrations applied successfully.");
            }
            else if (appliedMigrations.Count == 0)
            {                
                logger.LogInformation("No migrations found. Creating schema with EnsureCreated...");
                await db.Database.EnsureCreatedAsync();
                logger.LogInformation("Schema created successfully via EnsureCreated.");
            }
            else
            {
                logger.LogInformation("Database is up to date. {Count} migration(s) already applied.", appliedMigrations.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database initialisation failed. " + "Check connection string and ensure SQL Server is running.");
            throw;
        }
    }
}