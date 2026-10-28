namespace Fractals.Data.Entities;

public class TaskEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Params { get; set; } = "{}"; // JSONB
    public string Status { get; set; } = "QUEUED";
    public string Stage { get; set; } = "В черзі";
    public int Progress { get; set; } = 0;
    public string? AppServerId { get; set; }
    public bool IsCanceled { get; set; } = false;
    public long Cost { get; set; } = 0;
    public int Attempts { get; set; } = 0;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ResultPath { get; set; }
    public string? ThumbPath { get; set; }
    public int RowsTotal { get; set; } = 0;
    public int RowsDone { get; set; } = 0;
    public uint RowVersion { get; set; }

    public UserEntity? User { get; set; }
}

