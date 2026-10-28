using Fractals.AppServer;
using Fractals.AppServer.Services;
using Fractals.Core.Engine;
using Fractals.Data;
using Fractals.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Serilog;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// 1. Serilog Setup
builder.Services.AddSerilog((services, configuration) => configuration
    .Enrich.FromLogContext()
    .Enrich.WithProperty("AppServerId", builder.Configuration["APP_SERVER_ID"] ?? Environment.MachineName)
    .WriteTo.Console());

// 2. Database
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? "Host=localhost;Port=5432;Database=fractals_db;Username=postgres;Password=postgres_secure_password";

builder.Services.AddDbContext<FractalsDbContext>(options =>
{
    options.UseNpgsql(connectionString)
           .UseSnakeCaseNamingConvention();
});

builder.Services.AddScoped<ITaskRepository, TaskRepository>();
builder.Services.AddSingleton<IFractalEngine, FractalEngine>();

// 3. Redis
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
try
{
    var redis = ConnectionMultiplexer.Connect(new ConfigurationOptions
    {
        EndPoints = { redisConnection },
        AbortOnConnectFail = false,
        ConnectTimeout = 3000
    });
    builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
}
catch (Exception ex)
{
    Log.Warning(ex, "Redis недоступний для AppServer. Робота продовжується з чергою PostgreSQL.");
}

// 4. Background Services
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<HeartbeatService>();
builder.Services.AddHostedService<WatchdogService>();

var host = builder.Build();
host.Run();
