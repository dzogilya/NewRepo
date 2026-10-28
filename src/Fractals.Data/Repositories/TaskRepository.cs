using Fractals.Core.Models;
using Fractals.Data.Entities;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Fractals.Core.Models.TaskStatus;

namespace Fractals.Data.Repositories;

public class TaskRepository : ITaskRepository
{
    private readonly FractalsDbContext _context;

    public TaskRepository(FractalsDbContext context)
    {
        _context = context;
    }

    public async Task<TaskEntity?> ClaimNextTaskAsync(string appServerId, CancellationToken cancellationToken = default)
    {
        // For PostgreSQL: Atomic claim using FOR UPDATE SKIP LOCKED
        if (_context.Database.IsNpgsql())
        {
            const string sql = """
                UPDATE tasks
                SET status = 'Running',
                    stage = 'Обчислення рядків',
                    app_server_id = {0},
                    started_at = NOW(),
                    heartbeat_at = NOW(),
                    attempts = attempts + 1
                WHERE id = (
                    SELECT id FROM tasks
                    WHERE status = 'Queued' AND app_server_id IS NULL AND is_canceled = false
                    ORDER BY created_at, id
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                )
                RETURNING *;
                """;

            var claimed = await _context.Tasks
                .FromSqlRaw(sql, appServerId)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            return claimed.FirstOrDefault();
        }

        // Fallback for non-Postgres testing environments
        await using var tx = await _context.Database.BeginTransactionAsync(cancellationToken);
        var task = await _context.Tasks
            .Where(t => t.Status == "Queued" && t.AppServerId == null && !t.IsCanceled)
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (task == null) return null;

        task.Status = "Running";
        task.Stage = TaskStage.Running;
        task.AppServerId = appServerId;
        task.StartedAt = DateTimeOffset.UtcNow;
        task.HeartbeatAt = DateTimeOffset.UtcNow;
        task.Attempts++;

        await _context.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return task;
    }

    public async Task<bool> CheckCanceledAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        return await _context.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.IsCanceled)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task UpdateHeartbeatAndProgressAsync(
        Guid taskId,
        int progress,
        int rowsDone,
        CancellationToken cancellationToken = default)
    {
        await _context.Tasks
            .Where(t => t.Id == taskId && t.Status == "Running")
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Progress, progress)
                .SetProperty(t => t.RowsDone, rowsDone)
                .SetProperty(t => t.HeartbeatAt, DateTimeOffset.UtcNow),
                cancellationToken);
    }

    public async Task TransitionToAssemblingAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await _context.Tasks
            .Where(t => t.Id == taskId && t.Status == "Running")
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Assembling")
                .SetProperty(t => t.Stage, TaskStage.Assembling)
                .SetProperty(t => t.Progress, 99)
                .SetProperty(t => t.HeartbeatAt, DateTimeOffset.UtcNow),
                cancellationToken);
    }

    public async Task CompleteTaskAsync(
        Guid taskId,
        string resultPath,
        string thumbPath,
        CancellationToken cancellationToken = default)
    {
        await _context.Tasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Success")
                .SetProperty(t => t.Stage, TaskStage.Success)
                .SetProperty(t => t.Progress, 100)
                .SetProperty(t => t.FinishedAt, DateTimeOffset.UtcNow)
                .SetProperty(t => t.ResultPath, resultPath)
                .SetProperty(t => t.ThumbPath, thumbPath),
                cancellationToken);
    }

    public async Task FailTaskAsync(
        Guid taskId,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        await _context.Tasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Failed")
                .SetProperty(t => t.Stage, TaskStage.Failed)
                .SetProperty(t => t.ErrorCode, errorCode)
                .SetProperty(t => t.ErrorMessage, errorMessage)
                .SetProperty(t => t.FinishedAt, DateTimeOffset.UtcNow),
                cancellationToken);
    }

    public async Task<bool> CancelTaskAsync(
        Guid taskId,
        Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Tasks.Where(t => t.Id == taskId);
        if (userId.HasValue)
        {
            query = query.Where(t => t.UserId == userId.Value);
        }

        // If task is queued, mark as cancelled immediately. If running/assembling, mark is_canceled=true so worker stops.
        var task = await query.FirstOrDefaultAsync(cancellationToken);
        if (task == null || task.Status is "Success" or "Failed" or "Cancelled")
        {
            return false;
        }

        task.IsCanceled = true;
        if (task.Status == "Queued")
        {
            task.Status = "Cancelled";
            task.Stage = TaskStage.Cancelled;
            task.FinishedAt = DateTimeOffset.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> RequeueStaleTasksAsync(
        TimeSpan heartbeatTimeout,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(heartbeatTimeout);

        // Tasks exceeding max attempts fail permanently
        await _context.Tasks
            .Where(t => (t.Status == "Running" || t.Status == "Assembling") &&
                        t.HeartbeatAt != null && t.HeartbeatAt < cutoff &&
                        t.Attempts >= maxAttempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Failed")
                .SetProperty(t => t.Stage, TaskStage.Failed)
                .SetProperty(t => t.ErrorCode, "SERVER_TIMEOUT")
                .SetProperty(t => t.ErrorMessage, "Сервер обчислення перестав відповідати і ліміт повторних спроб вичерпано.")
                .SetProperty(t => t.FinishedAt, DateTimeOffset.UtcNow),
                cancellationToken);

        // Stale tasks with attempts remaining are re-queued to be picked up by another server
        return await _context.Tasks
            .Where(t => (t.Status == "Running" || t.Status == "Assembling") &&
                        t.HeartbeatAt != null && t.HeartbeatAt < cutoff &&
                        t.Attempts < maxAttempts && !t.IsCanceled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, "Queued")
                .SetProperty(t => t.Stage, TaskStage.Queued)
                .SetProperty(t => t.AppServerId, (string?)null)
                .SetProperty(t => t.Progress, 0)
                .SetProperty(t => t.RowsDone, 0),
                cancellationToken);
    }

    public async Task<int> GetQueuePositionAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _context.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task == null || task.Status != "Queued") return 0;

        return await _context.Tasks
            .AsNoTracking()
            .CountAsync(t => t.Status == "Queued" &&
                             (t.CreatedAt < task.CreatedAt || (t.CreatedAt == task.CreatedAt && t.Id.CompareTo(task.Id) < 0)),
                             cancellationToken) + 1;
    }

    public async Task<long> GetQueuePendingCostAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _context.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task == null || task.Status != "Queued") return 0;

        return await _context.Tasks
            .AsNoTracking()
            .Where(t => t.Status == "Queued" &&
                        (t.CreatedAt < task.CreatedAt || (t.CreatedAt == task.CreatedAt && t.Id.CompareTo(task.Id) < 0)))
            .SumAsync(t => t.Cost, cancellationToken);
    }

    public async Task<int> GetActiveTaskCountForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _context.Tasks
            .AsNoTracking()
            .CountAsync(t => t.UserId == userId && (t.Status == "Queued" || t.Status == "Running" || t.Status == "Assembling"),
                        cancellationToken);
    }

    public async Task<int> GetHistoryCountForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _context.Tasks
            .AsNoTracking()
            .CountAsync(t => t.UserId == userId, cancellationToken);
    }

    public async Task<int> GetGlobalQueueLengthAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Tasks
            .AsNoTracking()
            .CountAsync(t => t.Status == "Queued", cancellationToken);
    }
}

