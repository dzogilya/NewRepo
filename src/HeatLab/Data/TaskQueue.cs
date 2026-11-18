using Microsoft.EntityFrameworkCore;

namespace HeatLab.Data;

public static class TaskQueue
{
    public static async Task<string> CancelAsync(AppDb db, int taskId)
    {
        int fromQueue = await db.Tasks
            .Where(t => t.Id == taskId && t.Status == TaskState.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TaskState.Cancelled)
                .SetProperty(t => t.IsCanceled, true)
                .SetProperty(t => t.FinishedAt, DateTime.UtcNow));
        if (fromQueue > 0) return "Задачу прибрано з черги.";

        int running = await db.Tasks
            .Where(t => t.Id == taskId && t.Status == TaskState.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsCanceled, true));
        return running > 0 ? "Сервер зупинить обчислення протягом секунди." : "Задача вже завершена.";
    }

    public static Task<int> RequeueAsync(AppDb db, string? serverId) =>
        db.Tasks
            .Where(t => t.Status == TaskState.Running && (serverId == null || t.ServerId == serverId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, t => t.IsCanceled ? TaskState.Cancelled : TaskState.Queued)
                .SetProperty(t => t.ServerId, (string?)null)
                .SetProperty(t => t.Progress, 0)
                .SetProperty(t => t.Iteration, 0));

    public static async Task<Dictionary<int, (int Position, int WaitSeconds)>> QueueInfoAsync(AppDb db, int servers)
    {
        var active = await db.Tasks.AsNoTracking()
            .Where(t => t.Status == TaskState.Queued || t.Status == TaskState.Running)
            .OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.Status, t.Cost, t.Progress })
            .ToListAsync();
        var result = new Dictionary<int, (int, int)>();
        if (active.All(t => t.Status != TaskState.Queued)) return result;

        var recent = await db.Tasks.AsNoTracking()
            .Where(t => t.Status == TaskState.Success && t.StartedAt != null && t.FinishedAt != null && t.Cost > 0)
            .OrderByDescending(t => t.Id)
            .Take(20)
            .Select(t => new { t.Cost, t.StartedAt, t.FinishedAt })
            .ToListAsync();
        double secondsPerUnit = recent.Count > 0
            ? recent.Sum(t => (t.FinishedAt!.Value - t.StartedAt!.Value).TotalSeconds) / recent.Sum(t => (double)t.Cost)
            : 1.5e-9;

        var freeIn = active.Where(t => t.Status == TaskState.Running)
            .Select(t => t.Cost * secondsPerUnit * (100 - t.Progress) / 100)
            .ToList();
        while (freeIn.Count < Math.Max(1, servers)) freeIn.Add(0);

        int position = 0;
        foreach (var t in active.Where(t => t.Status == TaskState.Queued))
        {
            int first = freeIn.IndexOf(freeIn.Min());
            result[t.Id] = (++position, Math.Max(1, (int)Math.Ceiling(freeIn[first])));
            freeIn[first] += t.Cost * secondsPerUnit;
        }
        return result;
    }
}
