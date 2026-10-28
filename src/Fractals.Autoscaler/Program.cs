using Fractals.Autoscaler;
using Fractals.Data;
using Fractals.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Serilog;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, configuration) => configuration
    .Enrich.FromLogContext()
    .WriteTo.Console());

var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? "Host=localhost;Port=5432;Database=fractals_db;Username=postgres;Password=postgres_secure_password";

builder.Services.AddDbContext<FractalsDbContext>(options =>
{
    options.UseNpgsql(connectionString)
           .UseSnakeCaseNamingConvention();
});

builder.Services.AddScoped<ITaskRepository, TaskRepository>();

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
    Log.Warning(ex, "Redis недоступний для Autoscaler.");
}

builder.Services.AddHostedService<AutoscalerWorker>();

var host = builder.Build();
host.Run();
