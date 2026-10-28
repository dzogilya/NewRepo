namespace Fractals.Core.Models;

public class CalculationLimits
{
    public const string SectionName = "CalculationLimits";

    public long MaxCost { get; set; } = 500_000_000L;
    public int MaxWidth { get; set; } = 3000;
    public int MaxHeight { get; set; } = 3000;
    public int MaxIterations { get; set; } = 5000;
    public int MinWidth { get; set; } = 100;
    public int MinHeight { get; set; } = 100;
    public int MinIterations { get; set; } = 10;
    public int MaxActivePerUser { get; set; } = 2;
    public int MaxHistoryPerUser { get; set; } = 30;
    public int MaxQueueLength { get; set; } = 100;
    public int MaxTaskSeconds { get; set; } = 120;
}
