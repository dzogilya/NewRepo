namespace Fractals.Data.Entities;

public class ScalingEventEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Action { get; set; } = string.Empty; // "SCALE_UP" or "SCALE_DOWN"
    public string Reason { get; set; } = string.Empty;
    public int ServerCountBefore { get; set; }
    public int ServerCountAfter { get; set; }
}

