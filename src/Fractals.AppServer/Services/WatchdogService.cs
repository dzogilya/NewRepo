using Fractals.Data;
using Fractals.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Fractals.AppServer.Services;

public class WatchdogService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WatchdogService> _logger;

    public WatchdogService(
        IServiceProvider serviceProvider,
        ILogger<WatchdogService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<FractalsDbContext>();
                var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

                // Leader election via Postgres advisory lock
                if (dbContext.Database.IsNpgsql())
                {
                    bool isLeader = false;
                    await using var cmd = dbContext.Database.GetDbConnection().CreateCommand();
                    cmd.CommandText = "SELECT pg_try_advisory_lock(777888);";
                    if (cmd.Connection?.State != System.Data.ConnectionState.Open)
                    {
                        await dbContext.Database.OpenConnectionAsync(stoppingToken);
                    }

                    var result = await cmd.ExecuteScalarAsync(stoppingToken);
                    if (result is bool locked && locked)
                    {
                        isLeader = true;
                    }

                    if (!isLeader)
                    {
                        continue; // another node is the active watchdog leader
                    }
                }

                // Check for tasks with expired heartbeats (15 seconds threshold)
                int requeued = await taskRepo.RequeueStaleTasksAsync(
                    heartbeatTimeout: TimeSpan.FromSeconds(15),
                    maxAttempts: 3,
                    cancellationToken: stoppingToken);

                if (requeued > 0)
                {
                    _logger.LogWarning("Watchdog: виявлено завислі задачі без heartbeat. Повернуто в чергу: {Count}", requeued);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Помилка при роботі Watchdog.");
            }
        }
    }
}

