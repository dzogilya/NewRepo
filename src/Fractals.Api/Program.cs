using System.Text;
using System.Threading.RateLimiting;
using FluentValidation;
using Fractals.Api.Middleware;
using Fractals.Api.Services;
using Fractals.AppServer;
using Fractals.Core.Engine;
using Fractals.Core.Models;
using Fractals.Core.Validation;
using Fractals.Data;
using Fractals.Data.Entities;
using Fractals.Data.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

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
builder.Host.UseSerilog((context, services, configuration) => configuration
    .Enrich.FromLogContext()
    .Enrich.WithProperty("InstanceId", context.Configuration["INSTANCE_ID"] ?? Environment.MachineName)
    .WriteTo.Console());

// 2. Database Connection (Postgres in Docker / SQLite in local development)
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

// 3. Redis Connection & Data Protection (Graceful fallback if Redis is absent)
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    try
    {
        var redisMux = ConnectionMultiplexer.Connect(new ConfigurationOptions
        {
            EndPoints = { redisConnection },
            AbortOnConnectFail = true,
            ConnectTimeout = 1500
        });
        builder.Services.AddSingleton<IConnectionMultiplexer>(redisMux);

        // Shared Data Protection keys across api1 and api2
        builder.Services.AddDataProtection()
            .SetApplicationName("FractalsApp")
            .PersistKeysToStackExchangeRedis(redisMux, "DataProtection-Keys");
    }
    catch
    {
        Log.Information("Redis недоступний за адресою '{Endpoint}'. Використовується локальний кеш.", redisConnection);
    }
}

// 4. Options and Validation
builder.Services.Configure<CalculationLimits>(builder.Configuration.GetSection(CalculationLimits.SectionName));
builder.Services.AddSingleton<FractalParametersValidator>();

// 5. Application Services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IEtaService, EtaService>();
builder.Services.AddSingleton<IFractalEngine, FractalEngine>();

// In Development mode, also host Worker directly inside the web app for easy 1-click execution
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<Worker>();
}

// 6. Authentication (JWT + Cookie Fallback)
var jwtSecret = builder.Configuration["Jwt:Secret"] ?? "SuperSecretLongSecureKeyForJwtTokens1234567890!@#$";
var key = Encoding.UTF8.GetBytes(jwtSecret);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = false,
        ValidateAudience = false,
        ClockSkew = TimeSpan.Zero
    };

    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            if (string.IsNullOrEmpty(context.Token) && context.Request.Cookies.TryGetValue("auth_token", out var cookieToken))
            {
                context.Token = cookieToken;
            }
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

// 7. Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.Identity?.Name ?? httpContext.Request.Headers.Host.ToString(),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100,
                QueueLimit = 20,
                Window = TimeSpan.FromMinutes(1)
            }));
});

// 8. Health Checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<FractalsDbContext>("Database");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Fractals API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// 9. Database Auto-Migration / EnsureCreated
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FractalsDbContext>();
    var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(424242);");
            try
            {
                await db.Database.EnsureCreatedAsync();
                logger.LogInformation("PostgreSQL база даних готова.");
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(424242);");
            }
        }
        else
        {
            await db.Database.EnsureCreatedAsync();
            logger.LogInformation("Локальна база даних (SQLite/InMemory) готова.");
        }

        // Seed Admin user
        string adminUser = builder.Configuration["ADMIN_USERNAME"] ?? "admin";
        string adminPass = builder.Configuration["ADMIN_PASSWORD"] ?? "AdminSecurePassword123!";

        if (!await db.Users.AnyAsync(u => u.Username.ToLower() == adminUser.ToLower()))
        {
            var admin = new UserEntity
            {
                Id = Guid.NewGuid(),
                Username = adminUser,
                PasswordHash = auth.HashPassword(adminPass),
                Role = "admin",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Users.Add(admin);
            await db.SaveChangesAsync();
            logger.LogInformation("Створено адміністратора: {Username}", adminUser);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Помилка при ініціалізації БД.");
    }
}

// 10. Forwarded Headers for Nginx Proxy
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseMiddleware<ServedByMiddleware>();

// 11. Serve Frontend Static Files for Direct Local Run (without Nginx)
var frontendPath = Path.Combine(solutionRoot, "frontend");
if (!Directory.Exists(frontendPath))
{
    frontendPath = Path.Combine(builder.Environment.ContentRootPath, "frontend");
}

if (Directory.Exists(frontendPath))
{
    app.UseFileServer(new FileServerOptions
    {
        FileProvider = new PhysicalFileProvider(Path.GetFullPath(frontendPath)),
        RequestPath = "",
        EnableDefaultFiles = true
    });
}

// Serve Storage for Images
if (Directory.Exists(storagePath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(Path.GetFullPath(storagePath)),
        RequestPath = "/storage"
    });
}

app.UseSwagger(c => c.RouteTemplate = "api/docs/{documentName}/swagger.json");
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/api/docs/v1/swagger.json", "Fractals API v1");
    c.RoutePrefix = "api/docs";
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// 12. Health Check Endpoints
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/readyz");

app.MapControllers();

app.Run();

public partial class Program { }
