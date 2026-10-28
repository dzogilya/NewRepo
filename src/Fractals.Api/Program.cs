using System.Text;
using System.Threading.RateLimiting;
using FluentValidation;
using Fractals.Api.Middleware;
using Fractals.Api.Services;
using Fractals.Core.Models;
using Fractals.Core.Validation;
using Fractals.Data;
using Fractals.Data.Entities;
using Fractals.Data.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// 1. Serilog Setup
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("InstanceId", context.Configuration["INSTANCE_ID"] ?? Environment.MachineName)
    .WriteTo.Console());

// 2. Database Connection
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? "Host=localhost;Port=5432;Database=fractals_db;Username=postgres;Password=postgres_secure_password";

builder.Services.AddDbContext<FractalsDbContext>(options =>
{
    options.UseNpgsql(connectionString)
           .UseSnakeCaseNamingConvention();
});

builder.Services.AddScoped<ITaskRepository, TaskRepository>();

// 3. Redis Connection & Data Protection
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
IConnectionMultiplexer? redisMux = null;
try
{
    redisMux = ConnectionMultiplexer.Connect(new ConfigurationOptions
    {
        EndPoints = { redisConnection },
        AbortOnConnectFail = false,
        ConnectTimeout = 3000
    });
    builder.Services.AddSingleton<IConnectionMultiplexer>(redisMux);

    // Shared Data Protection keys across api1 and api2
    builder.Services.AddDataProtection()
        .SetApplicationName("FractalsApp")
        .PersistKeysToStackExchangeRedis(redisMux, "DataProtection-Keys");
}
catch (Exception ex)
{
    Log.Warning(ex, "Redis недоступний при старті. Використовуються локальні DataProtection ключі.");
}

// 4. Options and Validation
builder.Services.Configure<CalculationLimits>(builder.Configuration.GetSection(CalculationLimits.SectionName));
builder.Services.AddSingleton<FractalParametersValidator>();

// 5. Application Services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IEtaService, EtaService>();

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

    // Extract token from cookie if not present in Authorization header
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
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
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

// 9. Database Auto-Migration with Postgres Advisory Lock
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FractalsDbContext>();
    var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        if (db.Database.IsNpgsql())
        {
            // Acquire advisory lock 424242 so api1 and api2 don't collide
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(424242);");
            try
            {
                await db.Database.EnsureCreatedAsync();
                logger.LogInformation("База даних перевірена та готова.");
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(424242);");
            }
        }
        else
        {
            await db.Database.EnsureCreatedAsync();
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
            logger.LogInformation("Створено адміністратора за замовчуванням: {Username}", adminUser);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Помилка при ініціалізації/міграції БД.");
    }
}

// 10. Forwarded Headers for Nginx Proxy
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseMiddleware<ServedByMiddleware>();

app.UseSwagger(c => c.RouteTemplate = "api/docs/{documentName}/swagger.json");
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/api/docs/v1/swagger.json", "Fractals API v1");
    c.RoutePrefix = "api/docs";
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// 11. Health Check Endpoints
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/readyz");

app.MapControllers();

app.Run();

// For WebApplicationFactory in integration tests
public partial class Program { }
