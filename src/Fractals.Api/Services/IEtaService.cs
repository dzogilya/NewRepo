namespace Fractals.Api.Services;

public record QueueEtaInfo(
    int Position,
    long PendingCost,
    int EstimatedSeconds,
    string HumanEta);

public interface IEtaService
{
    Task<QueueEtaInfo> CalculateEtaAsync(Guid taskId, CancellationToken cancellationToken = default);
}
