using Fractals.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fractals.Data;

public class FractalsDbContext : DbContext
{
    public FractalsDbContext(DbContextOptions<FractalsDbContext> options)
        : base(options)
    {
    }

    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<ScalingEventEntity> ScalingEvents => Set<ScalingEventEntity>();
    public DbSet<AuditLogEntity> AuditLogs => Set<AuditLogEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Tasks Configuration
        modelBuilder.Entity<TaskEntity>(entity =>
        {
            entity.ToTable("tasks");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Type).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(30).IsRequired();
            entity.Property(e => e.Stage).HasMaxLength(100).IsRequired();
            entity.Property(e => e.AppServerId).HasMaxLength(100);
            
            if (Database.IsNpgsql())
            {
                entity.Property(e => e.Params).HasColumnType("jsonb");
            }
            entity.Property(e => e.RowVersion).IsRowVersion();

            entity.HasOne(e => e.User)
                  .WithMany(u => u.Tasks)
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
            entity.HasIndex(e => e.AppServerId);

            // Partial index for ultra-fast task claims on PostgreSQL
            if (Database.IsNpgsql())
            {
                entity.HasIndex(e => new { e.CreatedAt, e.Id })
                      .HasFilter("status = 'QUEUED' AND app_server_id IS NULL AND is_canceled = false")
                      .HasDatabaseName("ix_tasks_queued_claim");
            }
        });

        // Configure DateTimeOffset conversion for SQLite support in ORDER BY
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    {
                        property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
                    }
                }
            }
        }

        // Users Configuration
        modelBuilder.Entity<UserEntity>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => e.Username).IsUnique();
            entity.Property(e => e.Role).HasMaxLength(20).IsRequired();
        });

        // Scaling Events Configuration
        modelBuilder.Entity<ScalingEventEntity>(entity =>
        {
            entity.ToTable("scaling_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Action).HasMaxLength(50).IsRequired();
            entity.HasIndex(e => e.Timestamp);
        });

        // Audit Logs Configuration
        modelBuilder.Entity<AuditLogEntity>(entity =>
        {
            entity.ToTable("audit_log");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Action).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => e.Timestamp);
        });
    }
}

