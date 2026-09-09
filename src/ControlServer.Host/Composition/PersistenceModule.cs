using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Composition;

/// <summary>
/// The SQLite store and the ports served straight out of it.
/// </summary>
internal static class PersistenceModule
{
    internal static IServiceCollection AddControlServerPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string configuredConnection = configuration.GetConnectionString("ControlServer")
            ?? @"Data Source=%ProgramData%\8005\ControlServer\data\controlserver.db";
        string connectionString = ExpandDataSource(configuredConnection);
        services.AddDbContext<ControlServerDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<WireToGateStore>();
        services.AddScoped<IDemandAcceptanceStore>(sp => sp.GetRequiredService<WireToGateStore>());
        services.AddScoped<IMovementIntentStore>(sp => sp.GetRequiredService<WireToGateStore>());
        services.AddScoped<IPackageCapacityStore, PackageCapacityStore>();
        services.AddScoped<PackageCapacityImportService>();
        return services;
    }

    internal static async Task EnsureDatabaseAsync(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ControlServerDbContext dbContext = scope.ServiceProvider.GetRequiredService<ControlServerDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    private static string ExpandDataSource(string connectionString)
    {
        const string prefix = "Data Source=";
        if (!connectionString.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }
        string path = Environment.ExpandEnvironmentVariables(connectionString[prefix.Length..])
            .Replace('/', Path.DirectorySeparatorChar);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        return $"{prefix}{path}";
    }
}
