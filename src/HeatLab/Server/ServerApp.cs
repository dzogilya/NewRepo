using System.Text.Json;
using HeatLab.Data;
using HeatLab.Solver;
using Microsoft.EntityFrameworkCore;

namespace HeatLab.Server;

public static class ServerApp
{
    private static int _busy;

    public static async Task RunAsync(string serverId, int port, int gatewayPid)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders().AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        var app = builder.Build();
        var stopping = app.Lifetime.ApplicationStopping;

        app.MapGet("/healthz", () => "ok");
        app.MapPost("/run/{id:int}", (int id) =>
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return Results.Conflict();
            _ = Task.Run(async () =>
            {
                try { await ComputeAsync(id, serverId, app.Logger, stopping); }
                finally { _busy = 0; }
            });
            return Results.Accepted();
        });

        _ = Task.Run(async () =>
        {
            try { await System.Diagnostics.Process.GetProcessById(gatewayPid).WaitForExitAsync(); }
            catch (ArgumentException) { }
            app.Lifetime.StopApplication();
        });

        app.Logger.LogInformation("Сервер {Id} запущено (порт {Port}).", serverId, port);
        await app.RunAsync();
    }

    private static async Task ComputeAsync(int id, string serverId, ILogger log, CancellationToken stopping)
    {
        await using var db = AppDb.Open(AppDb.ConnectionString());
        var mine = db.Tasks.Where(t => t.Id == id && t.ServerId == serverId && t.Status == TaskState.Running);
        var task = await mine.AsNoTracking().FirstOrDefaultAsync();
        if (task == null) return;

        var p = JsonSerializer.Deserialize<HeatParameters>(task.Params, AppDb.Json)!;
        log.LogInformation("Задача #{Id}: сітка {N}×{N}, K = {K} — почав рахувати.", id, p.Size, p.Size, p.Iterations);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        limit.CancelAfter(TimeSpan.FromSeconds(Limits.MaxTaskSeconds));

        try
        {
            var result = await HeatSolver.SolveAsync(p, async iteration =>
            {
                int progress = Math.Min(99, (int)(100L * iteration / p.Iterations));
                await mine.ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Progress, progress)
                    .SetProperty(t => t.Iteration, iteration));
                if (await db.Tasks.Where(t => t.Id == id).Select(t => t.IsCanceled).FirstAsync())
                    throw new OperationCanceledException();
            }, limit.Token);

            await mine.ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TaskState.Success)
                .SetProperty(t => t.Progress, 100)
                .SetProperty(t => t.Iteration, result.Iterations)
                .SetProperty(t => t.Result, JsonSerializer.Serialize(result, AppDb.Json))
                .SetProperty(t => t.FinishedAt, DateTime.UtcNow));
            log.LogInformation("Задача #{Id}: готово за {Sec} с.", id, result.Seconds);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            await FinishAsync(mine, TaskState.Failed, $"Перевищено ліміт часу ({Limits.MaxTaskSeconds} с).");
            log.LogWarning("Задача #{Id}: перевищено ліміт часу.", id);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(mine, TaskState.Cancelled, null);
            log.LogInformation("Задача #{Id}: скасована.", id);
        }
        catch (Exception ex)
        {
            await FinishAsync(mine, TaskState.Failed, ex.Message);
            log.LogError("Задача #{Id}: помилка {Message}", id, ex.Message);
        }
    }

    private static Task FinishAsync(IQueryable<HeatTask> task, string status, string? error) =>
        task.ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Status, status)
            .SetProperty(t => t.Error, error)
            .SetProperty(t => t.FinishedAt, DateTime.UtcNow));
}
