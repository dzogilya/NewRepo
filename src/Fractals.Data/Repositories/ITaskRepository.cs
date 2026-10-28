using Fractals.Data.Entities;

namespace Fractals.Data.Repositories;

public interface ITaskRepository
{
    Task<TaskEntity?> ClaimNextTaskAsync(string appServerId, CancellationToken cancellationToken = default);
    Task<bool> CheckCanceledAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task UpdateHeartbeatAndProgressAsync(Guid taskId, int progress, int rowsDone, CancellationToken cancellationToken = default);
    Task TransitionToAssemblingAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task CompleteTaskAsync(Guid taskId, string resultPath, string thumbPath, CancellationToken cancellationToken = default);
    Task FailTaskAsync(Guid taskId, string errorCode, string errorMessage, CancellationToken cancellationToken = default);
    Task<bool> CancelTaskAsync(Guid taskId, Guid? userId = null, CancellationToken cancellationToken = default);
    Task<int> RequeueStaleTasksAsync(TimeSpan heartbeatTimeout, int maxAttempts = 3, CancellationToken cancellationToken = default);
    Task<int> GetQueuePositionAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task<long> GetQueuePendingCostAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task<int> GetActiveTaskCountForUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<int> GetHistoryCountForUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<int> GetGlobalQueueLengthAsync(CancellationToken cancellationToken = default);
}

