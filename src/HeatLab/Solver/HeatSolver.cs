using System.Diagnostics;

namespace HeatLab.Solver;

public record HeatParameters
{
    public int Size { get; init; } = 200;
    public int Iterations { get; init; } = 5000;
    public double Top { get; init; } = 100;
    public double Bottom { get; init; } = 0;
    public double Left { get; init; } = 0;
    public double Right { get; init; } = 0;

    public long Cost => (long)Size * Size * Iterations;

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Size < Limits.MinSize || Size > Limits.MaxSize)
            errors.Add($"Розмір сітки N має бути від {Limits.MinSize} до {Limits.MaxSize}.");
        if (Iterations < Limits.MinIterations || Iterations > Limits.MaxIterations)
            errors.Add($"Кількість ітерацій K має бути від {Limits.MinIterations} до {Limits.MaxIterations}.");
        foreach (double t in new[] { Top, Bottom, Left, Right })
        {
            if (!double.IsFinite(t) || t < Limits.MinTemperature || t > Limits.MaxTemperature)
            {
                errors.Add($"Температури країв мають бути від {Limits.MinTemperature} до {Limits.MaxTemperature} °C.");
                break;
            }
        }
        if (errors.Count == 0 && Cost > Limits.MaxCost)
            errors.Add($"Задача завелика: N·N·K = {Cost:N0}, максимум {Limits.MaxCost:N0}. Зменшіть N або K.");
        return errors;
    }
}

public record HeatResult(int MapSize, double[] Map, int Iterations, bool Converged, double Min, double Max, double Seconds);

public static class HeatSolver
{
    public const double Tolerance = 1e-3;
    private const int MapMaxSize = 100;
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(0.5);

    public static async Task<HeatResult> SolveAsync(
        HeatParameters p,
        Func<int, Task>? onProgress = null,
        CancellationToken cancellationToken = default,
        double tolerance = Tolerance)
    {
        var clock = Stopwatch.StartNew();
        int n = p.Size;

        double[] cur = CreateGrid(p);
        double[] next = (double[])cur.Clone();

        int iteration = 0;
        bool converged = false;
        TimeSpan lastReport = TimeSpan.Zero;

        while (iteration < p.Iterations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double maxChange = 0;
            for (int i = 1; i < n - 1; i++)
            {
                for (int j = 1; j < n - 1; j++)
                {
                    int k = i * n + j;
                    double v = 0.25 * (cur[k - 1] + cur[k + 1] + cur[k - n] + cur[k + n]);
                    maxChange = Math.Max(maxChange, Math.Abs(v - cur[k]));
                    next[k] = v;
                }
            }
            (cur, next) = (next, cur);
            iteration++;

            if (maxChange < tolerance)
            {
                converged = true;
                break;
            }

            if (onProgress != null && clock.Elapsed - lastReport >= ReportEvery)
            {
                lastReport = clock.Elapsed;
                await onProgress(iteration);
            }
        }

        return new HeatResult(
            MapSize: Math.Min(n, MapMaxSize),
            Map: Downsample(cur, n, Math.Min(n, MapMaxSize)),
            Iterations: iteration,
            Converged: converged,
            Min: Math.Round(cur.Min(), 2),
            Max: Math.Round(cur.Max(), 2),
            Seconds: Math.Round(clock.Elapsed.TotalSeconds, 2));
    }

    internal static double[] CreateGrid(HeatParameters p)
    {
        int n = p.Size;
        var g = new double[n * n];
        Array.Fill(g, Math.Min(Math.Min(p.Top, p.Bottom), Math.Min(p.Left, p.Right)));

        for (int x = 0; x < n; x++)
        {
            g[x] = p.Top;
            g[(n - 1) * n + x] = p.Bottom;
            g[x * n] = p.Left;
            g[x * n + n - 1] = p.Right;
        }

        g[0] = (p.Top + p.Left) / 2;
        g[n - 1] = (p.Top + p.Right) / 2;
        g[(n - 1) * n] = (p.Bottom + p.Left) / 2;
        g[n * n - 1] = (p.Bottom + p.Right) / 2;
        return g;
    }

    private static double[] Downsample(double[] grid, int n, int m)
    {
        var map = new double[m * m];
        for (int y = 0; y < m; y++)
        {
            int i = (int)Math.Round((double)y * (n - 1) / (m - 1));
            for (int x = 0; x < m; x++)
            {
                int j = (int)Math.Round((double)x * (n - 1) / (m - 1));
                map[y * m + x] = Math.Round(grid[i * n + j], 1);
            }
        }
        return map;
    }
}
