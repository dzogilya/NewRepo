using System.Security.Claims;
using System.Text.Json;
using Fractals.Api.Services;
using Fractals.Core.Models;
using Fractals.Core.Validation;
using Fractals.Data;
using Fractals.Data.Entities;
using Fractals.Data.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TaskStatus = Fractals.Core.Models.TaskStatus;

namespace Fractals.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class TasksController : ControllerBase
{
    private readonly FractalsDbContext _dbContext;
    private readonly ITaskRepository _taskRepository;
    private readonly IEtaService _etaService;
    private readonly FractalParametersValidator _validator;
    private readonly CalculationLimits _limits;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<TasksController> _logger;

    public TasksController(
        FractalsDbContext dbContext,
        ITaskRepository taskRepository,
        IEtaService etaService,
        FractalParametersValidator validator,
        IOptions<CalculationLimits> limits,
        ILogger<TasksController> logger,
        IConnectionMultiplexer? redis = null)
    {
        _dbContext = dbContext;
        _taskRepository = taskRepository;
        _etaService = etaService;
        _validator = validator;
        _limits = limits.Value;
        _logger = logger;
        _redis = redis;
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsAdmin => User.IsInRole("admin");

    [HttpPost]
    public async Task<IActionResult> CreateTask(
        [FromBody] FractalParameters parameters,
        CancellationToken cancellationToken)
    {
        // 1. Validate calculation parameters (width, height, iterations, max cost)
        var validationResult = await _validator.ValidateAsync(parameters, cancellationToken);
        if (!validationResult.IsValid)
        {
            return UnprocessableEntity(new
            {
                code = "LIMIT_EXCEEDED",
                errors = validationResult.Errors.Select(e => e.ErrorMessage)
            });
        }

        Guid userId = CurrentUserId;

        // 2. Atomic check of user active tasks limit
        int activeCount = await _taskRepository.GetActiveTaskCountForUserAsync(userId, cancellationToken);
        if (activeCount >= _limits.MaxActivePerUser)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                code = "USER_CONCURRENCY_LIMIT",
                message = $"Ви досягли ліміту активних задач ({_limits.MaxActivePerUser}). Зачекайте завершення попередніх або скасуйте їх."
            });
        }

        // 3. Check user history limit
        int historyCount = await _taskRepository.GetHistoryCountForUserAsync(userId, cancellationToken);
        if (historyCount >= _limits.MaxHistoryPerUser)
        {
            return StatusCode(StatusCodes.Status409Conflict, new
            {
                code = "HISTORY_LIMIT_REACHED",
                message = $"Ви досягли ліміту збережених задач ({_limits.MaxHistoryPerUser}). Будь ласка, видаліть застарілі результати з історії."
            });
        }

        // 4. Check global queue capacity
        int queueLength = await _taskRepository.GetGlobalQueueLengthAsync(cancellationToken);
        if (queueLength >= _limits.MaxQueueLength)
        {
            Response.Headers.Append("Retry-After", "30");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "GLOBAL_QUEUE_FULL",
                message = "Сервери перевантажені, черга очікування заповнена. Спробуйте пізніше."
            });
        }

        // 5. Create task record
        var task = new TaskEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = parameters.Type.ToString(),
            Params = JsonSerializer.Serialize(parameters),
            Status = "Queued",
            Stage = TaskStage.Queued,
            Progress = 0,
            Cost = parameters.CalculateCost(),
            CreatedAt = DateTimeOffset.UtcNow,
            QueuedAt = DateTimeOffset.UtcNow,
            RowsTotal = parameters.Height
        };

        _dbContext.Tasks.Add(task);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // 6. Publish poke to Redis
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                var sub = _redis.GetSubscriber();
                await sub.PublishAsync(RedisChannel.Literal("tasks:poke"), task.Id.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не вдалося надіслати поштовх у Redis. Спрацює резервний диспетчер БД.");
            }
        }

        var eta = await _etaService.CalculateEtaAsync(task.Id, cancellationToken);

        return AcceptedAtAction(nameof(GetTaskById), new { id = task.Id }, new
        {
            id = task.Id,
            status = task.Status,
            stage = task.Stage,
            cost = task.Cost,
            queuePosition = eta.Position,
            estimatedWait = eta.HumanEta
        });
    }

    [HttpGet]
    public async Task<IActionResult> GetTasks(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 12,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Tasks.AsNoTracking().Where(t => t.UserId == CurrentUserId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(t => t.Status.ToLower() == status.ToLower());
        }

        int total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new
            {
                t.Id,
                t.Type,
                t.Status,
                t.Stage,
                t.Progress,
                t.AppServerId,
                t.Cost,
                t.CreatedAt,
                t.FinishedAt,
                hasImage = t.ResultPath != null,
                hasThumb = t.ThumbPath != null
            })
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            total,
            page,
            pageSize,
            items
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTaskById(Guid id, CancellationToken cancellationToken)
    {
        var task = await _dbContext.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task == null) return NotFound(new { message = "Задачу не знайдено." });

        if (!IsAdmin && task.UserId != CurrentUserId)
        {
            return Forbid();
        }

        var eta = task.Status == "Queued" ? await _etaService.CalculateEtaAsync(task.Id, cancellationToken) : null;

        return Ok(new
        {
            task.Id,
            task.UserId,
            task.Type,
            task.Status,
            task.Stage,
            task.Progress,
            task.AppServerId,
            task.IsCanceled,
            task.Cost,
            task.Attempts,
            task.ErrorCode,
            task.ErrorMessage,
            task.CreatedAt,
            task.StartedAt,
            task.FinishedAt,
            task.RowsTotal,
            task.RowsDone,
            paramsData = JsonSerializer.Deserialize<FractalParameters>(task.Params),
            queuePosition = eta?.Position,
            estimatedWait = eta?.HumanEta
        });
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> CancelTask(Guid id, CancellationToken cancellationToken)
    {
        var task = await _dbContext.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task == null) return NotFound(new { message = "Задачу не знайдено." });

        if (!IsAdmin && task.UserId != CurrentUserId)
        {
            return Forbid();
        }

        bool success = await _taskRepository.CancelTaskAsync(id, IsAdmin ? null : CurrentUserId, cancellationToken);
        if (!success)
        {
            return BadRequest(new { message = "Неможливо скасувати задачу в поточному стані." });
        }

        // Notify via Redis
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                var sub = _redis.GetSubscriber();
                await sub.PublishAsync(RedisChannel.Literal($"task:cancel:{id}"), "canceled");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Помилка відправки сигналу скасування в Redis.");
            }
        }

        return Ok(new { message = "Задачу позначено як скасовану." });
    }

    [HttpGet("{id:guid}/events")]
    public async Task StreamEvents(Guid id, CancellationToken cancellationToken)
    {
        var task = await _dbContext.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task == null || (!IsAdmin && task.UserId != CurrentUserId))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        // Send initial state immediately from DB
        var eta = task.Status == "Queued" ? await _etaService.CalculateEtaAsync(task.Id, cancellationToken) : null;
        var initialData = new
        {
            taskId = task.Id,
            status = task.Status,
            stage = task.Stage,
            progress = task.Progress,
            rowsDone = task.RowsDone,
            rowsTotal = task.RowsTotal,
            appServerId = task.AppServerId,
            queuePosition = eta?.Position,
            estimatedWait = eta?.HumanEta
        };

        await Response.WriteAsync($"data: {JsonSerializer.Serialize(initialData)}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);

        if (task.Status is "Success" or "Failed" or "Cancelled")
        {
            return;
        }

        // Listen for Redis Pub/Sub events with periodic fallback heartbeat
        var channel = RedisChannel.Literal($"task:progress:{id}");
        Action<RedisChannel, RedisValue>? handler = null;

        var tcs = new TaskCompletionSource<bool>();
        using var reg = cancellationToken.Register(() => tcs.TrySetResult(true));

        if (_redis != null && _redis.IsConnected)
        {
            var sub = _redis.GetSubscriber();
            handler = async (_, value) =>
            {
                try
                {
                    await Response.WriteAsync($"data: {value}\n\n", cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);

                    var doc = JsonDocument.Parse(value.ToString());
                    if (doc.RootElement.TryGetProperty("status", out var s))
                    {
                        string statusStr = s.GetString() ?? "";
                        if (statusStr is "Success" or "Failed" or "Cancelled")
                        {
                            tcs.TrySetResult(true);
                        }
                    }
                }
                catch
                {
                    tcs.TrySetResult(true);
                }
            };

            await sub.SubscribeAsync(channel, handler);
        }

        // Polling loop as backup or if redis is not connected
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (!tcs.Task.IsCompleted && await timer.WaitForNextTickAsync(cancellationToken))
            {
                var current = await _dbContext.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
                if (current == null) break;

                var currentEta = current.Status == "Queued" ? await _etaService.CalculateEtaAsync(current.Id, cancellationToken) : null;
                var currentData = new
                {
                    taskId = current.Id,
                    status = current.Status,
                    stage = current.Stage,
                    progress = current.Progress,
                    rowsDone = current.RowsDone,
                    rowsTotal = current.RowsTotal,
                    appServerId = current.AppServerId,
                    queuePosition = currentEta?.Position,
                    estimatedWait = currentEta?.HumanEta
                };

                await Response.WriteAsync($"data: {JsonSerializer.Serialize(currentData)}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);

                if (current.Status is "Success" or "Failed" or "Cancelled")
                {
                    break;
                }
            }
        }
        finally
        {
            if (_redis != null && _redis.IsConnected && handler != null)
            {
                var sub = _redis.GetSubscriber();
                await sub.UnsubscribeAsync(channel, handler);
            }
        }
    }

    [HttpGet("{id:guid}/image")]
    public async Task<IActionResult> GetTaskImage(Guid id, CancellationToken cancellationToken)
    {
        var task = await _dbContext.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task == null || string.IsNullOrEmpty(task.ResultPath))
        {
            return NotFound(new { message = "Зображення ще не згенеровано." });
        }

        if (!IsAdmin && task.UserId != CurrentUserId) return Forbid();

        if (!System.IO.File.Exists(task.ResultPath))
        {
            return NotFound(new { message = "Файл зображення не знайдено на диску." });
        }

        var stream = System.IO.File.OpenRead(task.ResultPath);
        return File(stream, "image/png");
    }

    [HttpGet("{id:guid}/thumb")]
    public async Task<IActionResult> GetTaskThumb(Guid id, CancellationToken cancellationToken)
    {
        var task = await _dbContext.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task == null || string.IsNullOrEmpty(task.ThumbPath))
        {
            return NotFound(new { message = "Мініатюра ще не згенерована." });
        }

        if (!IsAdmin && task.UserId != CurrentUserId) return Forbid();

        if (!System.IO.File.Exists(task.ThumbPath))
        {
            return NotFound(new { message = "Файл мініатюри не знайдено на диску." });
        }

        var stream = System.IO.File.OpenRead(task.ThumbPath);
        return File(stream, "image/png");
    }
}
