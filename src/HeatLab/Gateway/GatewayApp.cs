using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HeatLab.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

namespace HeatLab.Gateway;

public static class GatewayApp
{
    public const int HttpPort = 7080;
    public const int HttpsPort = 7443;

    public static async Task RunAsync(string[] args)
    {
        Console.Title = "HeatLab — шлюз і балансувальник";

        var ports = new[] { HttpPort, HttpsPort }
            .Concat(Enumerable.Range(Limits.FirstServerPort, Limits.MaxServers));
        var busy = ports.Where(IsPortBusy).ToList();
        if (busy.Count > 0)
        {
            throw new InvalidOperationException(
                $"Порти {string.Join(", ", busy)} зайняті іншою програмою (можливо, стара версія HeatLab). " +
                "Закрийте її і запустіть run.bat знову.");
        }

        await using (var db = AppDb.Open(AppDb.ConnectionString()))
        {
            await AppDb.InitializeAsync(db);
            await TaskQueue.RequeueAsync(db, null);
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders().AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        var certificate = LoadOrCreateCertificate(AppDb.StorageDirectory());
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenLocalhost(HttpPort);
            k.ListenLocalhost(HttpsPort, o => o.UseHttps(certificate));
        });
        builder.Services.AddHttpsRedirection(o =>
        {
            o.HttpsPort = HttpsPort;
            o.RedirectStatusCode = StatusCodes.Status301MovedPermanently;
        });

        builder.Services.AddDbContext<AppDb>(o => o.UseSqlite(AppDb.ConnectionString()));
        builder.Services.AddControllers();
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(AppDb.StorageDirectory(), "keys")));
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "heatlab_auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
            });
        builder.Services.AddAuthorization();

        builder.Services.AddSingleton<Balancer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Balancer>());

        var app = builder.Build();

        app.UseHttpsRedirection();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
            await next();
        });

        var frontend = new PhysicalFileProvider(Path.Combine(AppDb.RootDirectory(), "frontend"));
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontend });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = frontend,
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
        });

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        app.Logger.LogInformation("Сайт: https://localhost:{Port}", HttpsPort);
        if (!args.Contains("--no-browser")) app.Lifetime.ApplicationStarted.Register(OpenBrowser);

        await app.RunAsync();
    }

    private static void OpenBrowser()
    {
        try { Process.Start(new ProcessStartInfo($"https://localhost:{HttpsPort}") { UseShellExecute = true }); }
        catch { }
    }

    public static bool IsPortBusy(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    private static X509Certificate2 LoadOrCreateCertificate(string storage)
    {
        const string password = "heatlab";
        string pfxPath = Path.Combine(storage, "gateway-cert.pfx");

        if (File.Exists(pfxPath))
        {
            try
            {
                var existing = new X509Certificate2(pfxPath, password);
                if (existing.NotAfter > DateTime.Now.AddDays(7)) return existing;
            }
            catch (CryptographicException)
            {
            }
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        using var generated = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        byte[] pfx = generated.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(pfxPath, pfx);
        File.WriteAllBytes(Path.Combine(storage, "gateway-cert.cer"), generated.Export(X509ContentType.Cert));
        return new X509Certificate2(pfx, password);
    }
}
