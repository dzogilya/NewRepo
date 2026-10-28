using Fractals.AppServer;
using Fractals.AppServer.Services;
using Fractals.Core.Engine;
using Fractals.Data;
using Fractals.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Serilog;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// Find solution root directory reliably
static string FindSolutionRoot(string startDir)
{
    var current = new DirectoryInfo(startDir);
    while (current != null)
    {
        if (File.Exists(Path.Combine(current.FullName, "Fractals.slnx")) ||
            File.Exists(Path.Combine(current.FullName, "docker-compose.yml")))
        {
            return current.FullName;
        }
        current = current.Parent;
    }
    return startDir;
}

var solutionRoot = FindSolutionRoot(AppContext.BaseDirectory);
var storagePath = Path.Combine(solutionRoot, "storage");
Directory.CreateDirectory(storagePath);
var sqliteDbPath = Path.Combine(storagePath, "fractals.db");

// 1. Serilog Setup
builder.Services.AddSerilog((services, configuration) => configuration
    .Enrich.FromLogContext()
    .Enrich.WithProperty("AppServerId", builder.Configuration["APP_SERVER_ID"] ?? Environment.MachineName)
    .WriteTo.Console());

// 2. Database (PostgreSQL in Docker / SQLite in local development)
var rawConnection = builder.Configuration.GetConnectionString("Database");
string connectionString;
if (string.IsNullOrWhiteSpace(rawConnection) || rawConnection.Contains(".db") || rawConnection.Contains("sqlite", StringComparison.OrdinalIgnoreCase))
{
    connectionString = $"Data Source={sqliteDbPath}";
}
else
{
    connectionString = rawConnection;
}

builder.Services.AddDbContext<FractalsDbContext>(options =>
{
    if (connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) && connectionString.Contains(".db"))
    {
        options.UseSqlite(connectionString);
    }
    else
    {
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
    }
});

builder.Services.AddScoped<ITaskRepository, TaskRepository>();
builder.Services.AddSingleton<IFractalEngine, FractalEngine>();

// 3. Redis (Safe fallback)
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    try
    {
        var redis = ConnectionMultiplexer.Connect(new ConfigurationOptions
        {
            EndPoints = { redisConnection },
            AbortOnConnectFail = true,
            ConnectTimeout = 1500
        });
        builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
    }
    catch
    {
        Log.Information("Redis недоступний. AppServer працюватиме з чергою бази даних.");
    }
}

// 4. Background Services
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<HeartbeatService>();
builder.Services.AddHostedService<WatchdogService>();

var host = builder.Build();
host.Run();
