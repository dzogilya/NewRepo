using Fractals.Data.Repositories;
using StackExchange.Redis;

namespace Fractals.Api.Services;

public class EtaService : IEtaService
{
    private readonly ITaskRepository _taskRepository;
    private readonly IConnectionMultiplexer? _redis;
    private readonly IConfiguration _configuration;

    public EtaService(
        ITaskRepository taskRepository,
        IConfiguration configuration,
        IConnectionMultiplexer? redis = null)
    {
        _taskRepository = taskRepository;
        _configuration = configuration;
        _redis = redis;
    }

    public async Task<QueueEtaInfo> CalculateEtaAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        int position = await _taskRepository.GetQueuePositionAsync(taskId, cancellationToken);
        if (position <= 0)
        {
            return new QueueEtaInfo(0, 0, 0, "Виконується або завершено");
        }

        long pendingCost = await _taskRepository.GetQueuePendingCostAsync(taskId, cancellationToken);

        // Count active app servers via Redis heartbeat keys, fallback to 2
        int aliveServers = 2;
        if (_redis != null && _redis.IsConnected)
        {
            try
            {
                var server = _redis.GetServer(_redis.GetEndPoints()[0]);
                int keysCount = server.Keys(pattern: "heartbeat:app:*").Count();
                if (keysCount > 0)
                {
                    aliveServers = keysCount;
                }
            }
            catch
            {
                aliveServers = 2;
            }
        }

        int concurrency = _configuration.GetValue<int>("APP_CONCURRENCY", 2);
        int totalSlots = Math.Max(1, aliveServers * concurrency);

        // Baseline cost throughput: ~2,500,000 cost units per second per CPU core/slot
        const long baselineCostPerSlotSec = 2_500_000L;
        long totalThroughputPerSec = totalSlots * baselineCostPerSlotSec;

        int estimatedSeconds = (int)Math.Max(1, (pendingCost + totalThroughputPerSec - 1) / totalThroughputPerSec);

        string humanEta = estimatedSeconds switch
        {
            < 60 => $"~{estimatedSeconds} с",
            < 3600 => $"~{estimatedSeconds / 60} хв {estimatedSeconds % 60} с",
            _ => $"~{estimatedSeconds / 3600} год {(estimatedSeconds % 3600) / 60} хв"
        };

        return new QueueEtaInfo(position, pendingCost, estimatedSeconds, humanEta);
    }
}

