using System.Text.Json;
using Fractals.Core.Engine;
using Fractals.Core.Models;
using Fractals.Data.Entities;
using Fractals.Data.Repositories;
using StackExchange.Redis;

namespace Fractals.AppServer;

public class Worker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<Worker> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly string _appServerId;
    private readonly int _concurrency;
    private readonly int _cpuThreads;
    private readonly string _storagePath;
    private readonly SemaphoreSlim _semaphore;

    public static int ActiveRunningTasksCount;

    public Worker(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<Worker> logger,
        IConnectionMultiplexer? redis = null)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
        _redis = redis;

        _appServerId = _configuration["APP_SERVER_ID"] ?? Environment.MachineName;
        _concurrency = _configuration.GetValue<int>("APP_CONCURRENCY", 2);
        _cpuThreads = _configuration.GetValue<int>("APP_CPU_THREADS", Environment.ProcessorCount);

        if (!string.IsNullOrWhiteSpace(_configuration["STORAGE_PATH"]))
        {
            _storagePath = _configuration["STORAGE_PATH"]!;
        }
        else
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string root = Directory.GetCurrentDirectory();
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Fractals.slnx")) ||
                    File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")))
                {
                    root = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }
            _storagePath = Path.Combine(root, "storage");
        }

        _semaphore = new SemaphoreSlim(_concurrency, _concurrency);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_storagePath);
        _logger.LogInformation("Обчислювальний сервер '{ServerId}' запущено. Слоти: {Slots}, Потоки: {Threads}",
            _appServerId, _concurrency, _cpuThreads);

        var triggerChannel = System.Threading.Channels.Channel.CreateBounded<bool>(100);

        // 1. Subscribe to Redis poke
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                var sub = _redis.GetSubscriber();
                await sub.SubscribeAsync(RedisChannel.Literal("tasks:poke"), (_, _) =>
                {
                    triggerChannel.Writer.TryWrite(true);
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не вдалося підписатися на Redis tasks:poke. Застосовується резервний таймер.");
            }
        }

        // 2. Periodic polling timer as reliable backup (DB is the source of truth)
        _ = Task.Run(async () =>
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                triggerChannel.Writer.TryWrite(true);
            }
        }, stoppingToken);

        // 3. Main processing loop
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for available execution slot
            await _semaphore.WaitAsync(stoppingToken);

            // Drain any pending trigger signal
            triggerChannel.Reader.TryRead(out _);

            TaskEntity? claimedTask = null;
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
                claimedTask = await repository.ClaimNextTaskAsync(_appServerId, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Помилка під час спроби зарезервувати задачу.");
            }

            if (claimedTask == null)
            {
                // No tasks waiting in queue, release slot and wait for next signal
                _semaphore.Release();
                await triggerChannel.Reader.WaitToReadAsync(stoppingToken);
                continue;
            }

            // Execute task in background task so other slots can be picked up immediately
            _ = ProcessTaskAsync(claimedTask, stoppingToken);
        }
    }

    private async Task ProcessTaskAsync(TaskEntity task, CancellationToken hostStoppingToken)
    {
        Interlocked.Increment(ref ActiveRunningTasksCount);
        _logger.LogInformation("Сервер '{ServerId}' розпочав обчислення задачі {TaskId}", _appServerId, task.Id);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(hostStoppingToken);
        int maxTaskSeconds = _configuration.GetValue<int>("CalculationLimits:MaxTaskSeconds", 120);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(maxTaskSeconds));

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var engine = scope.ServiceProvider.GetRequiredService<IFractalEngine>();

        try
        {
            var parameters = JsonSerializer.Deserialize<FractalParameters>(task.Params)
                ?? new FractalParameters();

            DateTimeOffset lastProgressUpdate = DateTimeOffset.MinValue;

            var result = await engine.RenderAsync(
                parameters,
                onProgressAsync: async (rowsDone, totalRows) =>
                {
                    int progress = (int)Math.Clamp((double)rowsDone / totalRows * 100.0, 0, 99);

                    // Throttle DB updates to once per 500ms
                    var now = DateTimeOffset.UtcNow;
                    if ((now - lastProgressUpdate).TotalMilliseconds > 500 || rowsDone == totalRows)
                    {
                        lastProgressUpdate = now;
                        await repository.UpdateHeartbeatAndProgressAsync(task.Id, progress, rowsDone, linkedCts.Token);
                        await PublishProgressEventAsync(task.Id, "Running", TaskStage.Running, progress, rowsDone, totalRows);
                    }
                },
                checkCanceledAsync: async () =>
                {
                    return await repository.CheckCanceledAsync(task.Id, hostStoppingToken);
                },
                stripHeight: 64,
                maxDegreeOfParallelism: _cpuThreads,
                cancellationToken: linkedCts.Token);

            // Transition to Assembling stage
            await repository.TransitionToAssemblingAsync(task.Id, hostStoppingToken);
            await PublishProgressEventAsync(task.Id, "Assembling", TaskStage.Assembling, 99, task.RowsTotal, task.RowsTotal);

            // Save PNG & Thumbnail to shared storage
            string fullPath = Path.Combine(_storagePath, $"{task.Id}.png");
            string thumbPath = Path.Combine(_storagePath, $"{task.Id}_thumb.png");

            await File.WriteAllBytesAsync(fullPath, result.ImagePng, hostStoppingToken);
            await File.WriteAllBytesAsync(thumbPath, result.ThumbnailPng, hostStoppingToken);

            // Mark Success
            await repository.CompleteTaskAsync(task.Id, fullPath, thumbPath, hostStoppingToken);
            await PublishProgressEventAsync(task.Id, "Success", TaskStage.Success, 100, task.RowsTotal, task.RowsTotal);

            _logger.LogInformation("Задача {TaskId} успішно завершена за {ElapsedMs} мс",
                task.Id, result.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            if (hostStoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning("Зупинка хоста під час задачі {TaskId}. Повертаємо в чергу.", task.Id);
            }
            else if (await repository.CheckCanceledAsync(task.Id, CancellationToken.None))
            {
                _logger.LogInformation("Задачу {TaskId} скасовано користувачем/адміном.", task.Id);
                await PublishProgressEventAsync(task.Id, "Cancelled", TaskStage.Cancelled, 0, 0, task.RowsTotal);
            }
            else
            {
                _logger.LogWarning("Перевищено ліміт часу виконання для задачі {TaskId}.", task.Id);
                await repository.FailTaskAsync(task.Id, "TIME_LIMIT_EXCEEDED",
                    "Час обчислення перевищив максимально дозволений ліміт.", CancellationToken.None);
                await PublishProgressEventAsync(task.Id, "Failed", TaskStage.Failed, 0, 0, task.RowsTotal);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критична помилка при обчисленні задачі {TaskId}", task.Id);
            await repository.FailTaskAsync(task.Id, "CALCULATION_ERROR", ex.Message, CancellationToken.None);
            await PublishProgressEventAsync(task.Id, "Failed", TaskStage.Failed, 0, 0, task.RowsTotal);
        }
        finally
        {
            Interlocked.Decrement(ref ActiveRunningTasksCount);
            _semaphore.Release();
        }
    }

    private async Task PublishProgressEventAsync(
        Guid taskId,
        string status,
        string stage,
        int progress,
        int rowsDone,
        int rowsTotal)
    {
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                var sub = _redis.GetSubscriber();
                var data = new
                {
                    taskId,
                    status,
                    stage,
                    progress,
                    rowsDone,
                    rowsTotal,
                    appServerId = _appServerId
                };
                await sub.PublishAsync(RedisChannel.Literal($"task:progress:{taskId}"), JsonSerializer.Serialize(data));
            }
            catch
            {
                // Non-critical: clients fallback to DB polling if Redis event is missed
            }
        }
    }
}
