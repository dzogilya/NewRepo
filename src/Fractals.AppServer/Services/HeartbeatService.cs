using System.Diagnostics;
using System.Text.Json;
using StackExchange.Redis;

namespace Fractals.AppServer.Services;

public class HeartbeatService : BackgroundService
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly string _serverId;
    private readonly ILogger<HeartbeatService> _logger;

    public HeartbeatService(
        IConfiguration configuration,
        ILogger<HeartbeatService> logger,
        IConnectionMultiplexer? redis = null)
    {
        _serverId = configuration["APP_SERVER_ID"] ?? Environment.MachineName;
        _logger = logger;
        _redis = redis;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_redis == null || !_redis.IsConnected)
        {
            _logger.LogInformation("HeartbeatService: Redis відсутній, звітність heartbeats через Redis вимкнена.");
            return;
        }

        var db = _redis.GetDatabase();
        var key = $"heartbeat:app:{_serverId}";
        var process = Process.GetCurrentProcess();
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                process.Refresh();
                double ramMb = process.WorkingSet64 / (1024.0 * 1024.0);

                var status = new
                {
                    serverId = _serverId,
                    serverType = "AppServer",
                    status = "Alive",
                    ramUsageMb = Math.Round(ramMb, 1),
                    activeTasks = Worker.ActiveRunningTasksCount,
                    lastHeartbeat = DateTimeOffset.UtcNow
                };

                await db.StringSetAsync(key, JsonSerializer.Serialize(status), TimeSpan.FromSeconds(6));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Помилка відправки сигналу життєдіяльності (heartbeat).");
            }
        }
    }
}

