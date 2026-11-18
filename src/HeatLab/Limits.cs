namespace HeatLab;

public static class Limits
{
    public const int MinSize = 10;
    public const int MaxSize = 1000;
    public const int MinIterations = 10;
    public const int MaxIterations = 50_000;
    public const double MinTemperature = -273;
    public const double MaxTemperature = 5000;

    public const long MaxCost = 20_000_000_000L;

    public const int MaxTaskSeconds = 120;

    public const int MaxActivePerUser = 5;
    public const int MaxHistoryPerUser = 50;
    public const int MaxQueueLength = 4;

    public const int BaseServers = 2;
    public const int MaxServers = 4;
    public const int FirstServerPort = 5101;
}
