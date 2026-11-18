using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HeatLab.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DbSet<User> Users => Set<User>();
    public DbSet<HeatTask> Tasks => Set<HeatTask>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>().HasIndex(u => u.Username).IsUnique();
        model.Entity<HeatTask>().HasIndex(t => t.Status);
        model.Entity<HeatTask>().HasIndex(t => t.UserId);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder config)
    {
        config.Properties<DateTime>().HaveConversion<UtcConverter>();
    }

    private class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    public static string RootDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HeatLab.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    public static string StorageDirectory()
    {
        string path = Path.Combine(RootDirectory(), "storage");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string ConnectionString(string? file = null) =>
        $"Data Source={file ?? Path.Combine(StorageDirectory(), "heatlab.sqlite")};Default Timeout=30";

    public static AppDb Open(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDb>().UseSqlite(connectionString).Options);

    public static async Task InitializeAsync(AppDb db)
    {
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        if (!await db.Users.AnyAsync(u => u.Role == "admin"))
        {
            var admin = new User { Username = "admin", Role = "admin" };
            admin.PasswordHash = new PasswordHasher<User>().HashPassword(admin, "admin123");
            db.Users.Add(admin);
            await db.SaveChangesAsync();
        }
    }
}
