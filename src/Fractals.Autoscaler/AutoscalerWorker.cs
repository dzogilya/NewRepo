using Docker.DotNet;
using Docker.DotNet.Models;
using Fractals.Data;
using Fractals.Data.Entities;
using Fractals.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Fractals.Autoscaler;

public class AutoscalerWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AutoscalerWorker> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly DockerClient? _dockerClient;

    private readonly int _minServers;
    private readonly int _maxServers;
    private readonly int _queueThreshold;
    private readonly int _scaleDownIdleSeconds;

    private DateTimeOffset _lastScaleAction = DateTimeOffset.MinValue;
    private readonly TimeSpan _cooldown = TimeSpan.FromSeconds(20);

    public AutoscalerWorker(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<AutoscalerWorker> logger,
        IConnectionMultiplexer? redis = null)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
        _redis = redis;

        _minServers = _configuration.GetValue<int>("MIN_APP_SERVERS", 2);
        _maxServers = _configuration.GetValue<int>("MAX_APP_SERVERS", 6);
        _queueThreshold = _configuration.GetValue<int>("SCALE_UP_QUEUE_THRESHOLD", 2);
        _scaleDownIdleSeconds = _configuration.GetValue<int>("SCALE_DOWN_IDLE_SECONDS", 30);

        try
        {
            Uri dockerUri = OperatingSystem.IsWindows()
                ? new Uri("npipe://./pipe/docker_engine")
                : new Uri("unix:///var/run/docker.sock");

            _dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не вдалося ініціалізувати DockerClient. Автоскейлер працюватиме в режимі симуляції.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Autoscaler запущено. Межі: MIN={Min}, MAX={Max}, Поріг черги={Threshold}",
            _minServers, _maxServers, _queueThreshold);

        var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await EvaluateScalingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Помилка під час оцінки метрик масштабування.");
            }
        }
    }

    private async Task EvaluateScalingAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var db = scope.ServiceProvider.GetRequiredService<FractalsDbContext>();

        int queueLength = await repository.GetGlobalQueueLengthAsync(cancellationToken);
        int currentServerCount = await GetActiveAppServersCountAsync(db, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (now - _lastScaleAction < _cooldown)
        {
            return; // Cooldown against flapping
        }

        // 1. Scale UP condition
        if (queueLength >= _queueThreshold && currentServerCount < _maxServers)
        {
            int targetCount = Math.Min(currentServerCount + 1, _maxServers);
            string newServerId = $"app-node-{targetCount}";

            _logger.LogInformation("Масштабування вгору (SCALE_UP): черга={Queue}, сервери {Before}->{After}",
                queueLength, currentServerCount, targetCount);

            await LaunchNewAppServerContainerAsync(newServerId, cancellationToken);

            db.ScalingEvents.Add(new ScalingEventEntity
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                Action = "SCALE_UP",
                Reason = $"Черга ({queueLength}) перевищила поріг ({_queueThreshold}).",
                ServerCountBefore = currentServerCount,
                ServerCountAfter = targetCount
            });
            await db.SaveChangesAsync(cancellationToken);
            _lastScaleAction = DateTimeOffset.UtcNow;
        }
        // 2. Scale DOWN condition
        else if (queueLength == 0 && currentServerCount > _minServers)
        {
            // Find an idle app server with 0 running tasks
            var idleServer = await FindIdleAppServerAsync(db, cancellationToken);
            if (idleServer != null)
            {
                int targetCount = currentServerCount - 1;
                _logger.LogInformation("Масштабування вниз (SCALE_DOWN): зупинка бездіяльного сервера {ServerId}", idleServer);

                await StopAppServerContainerAsync(idleServer, cancellationToken);

                db.ScalingEvents.Add(new ScalingEventEntity
                {
                    Id = Guid.NewGuid(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Action = "SCALE_DOWN",
                    Reason = $"Черга порожня, сервер {idleServer} бездіє.",
                    ServerCountBefore = currentServerCount,
                    ServerCountAfter = targetCount
                });
                await db.SaveChangesAsync(cancellationToken);
                _lastScaleAction = DateTimeOffset.UtcNow;
            }
        }
    }

    private async Task<int> GetActiveAppServersCountAsync(FractalsDbContext db, CancellationToken cancellationToken)
    {
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                var server = _redis.GetServer(_redis.GetEndPoints()[0]);
                int keys = server.Keys(pattern: "heartbeat:app:*").Count();
                if (keys > 0) return keys;
            }
            catch { }
        }

        var runningServers = await db.Tasks
            .AsNoTracking()
            .Where(t => t.AppServerId != null && (t.Status == "Running" || t.Status == "Assembling"))
            .Select(t => t.AppServerId)
            .Distinct()
            .CountAsync(cancellationToken);

        return Math.Max(runningServers, _minServers);
    }

    private async Task<string?> FindIdleAppServerAsync(FractalsDbContext db, CancellationToken cancellationToken)
    {
        var busyServers = await db.Tasks
            .AsNoTracking()
            .Where(t => t.AppServerId != null && (t.Status == "Running" || t.Status == "Assembling"))
            .Select(t => t.AppServerId!)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Don't terminate core base servers (app-node-1, app-node-2)
        var candidates = Enumerable.Range(3, _maxServers)
            .Select(i => $"app-node-{i}")
            .Where(s => !busyServers.Contains(s))
            .ToList();

        return candidates.FirstOrDefault();
    }

    private async Task LaunchNewAppServerContainerAsync(string serverId, CancellationToken cancellationToken)
    {
        if (_dockerClient == null) return;

        try
        {
            var createParams = new CreateContainerParameters
            {
                Image = "fractals-appserver:latest",
                Name = $"fractals-{serverId}",
                Env = new List<string>
                {
                    $"APP_SERVER_ID={serverId}",
                    "APP_CONCURRENCY=2",
                    "APP_CPU_THREADS=2",
                    "ConnectionStrings__Database=Host=postgres;Port=5432;Database=fractals_db;Username=postgres;Password=postgres_secure_password",
                    "ConnectionStrings__Redis=redis:6379"
                },
                Labels = new Dictionary<string, string>
                {
                    { "com.fractals.project", "autoscaled" }
                }
            };

            var container = await _dockerClient.Containers.CreateContainerAsync(createParams, cancellationToken);
            await _dockerClient.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), cancellationToken);
            _logger.LogInformation("Docker: успішно запущено контейнер {Name}", createParams.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker API: не вдалося створити або запустити контейнер.");
        }
    }

    private async Task StopAppServerContainerAsync(string serverId, CancellationToken cancellationToken)
    {
        if (_dockerClient == null) return;

        try
        {
            var containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = true }, cancellationToken);

            var target = containers.FirstOrDefault(c => c.Names.Any(n => n.Contains(serverId)));
            if (target != null)
            {
                await _dockerClient.Containers.StopContainerAsync(target.ID,
                    new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, cancellationToken);
                await _dockerClient.Containers.RemoveContainerAsync(target.ID,
                    new ContainerRemoveParameters { Force = true }, cancellationToken);
                _logger.LogInformation("Docker: успішно зупинено та видалено контейнер {ServerId}", serverId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker API: не вдалося зупинити контейнер {ServerId}", serverId);
        }
    }
}
