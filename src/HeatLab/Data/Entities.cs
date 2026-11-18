namespace HeatLab.Data;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "user";
}

public static class TaskState
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Success = "Success";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public class HeatTask
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }

    public string Params { get; set; } = "{}";
    public long Cost { get; set; }

    public string Status { get; set; } = TaskState.Queued;
    public int Progress { get; set; }
    public int Iteration { get; set; }

    public string? ServerId { get; set; }

    public bool IsCanceled { get; set; }

    public string? Result { get; set; }
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
