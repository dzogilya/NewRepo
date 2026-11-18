using HeatLab.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HeatLab.Gateway;

[Authorize(Roles = "admin")]
[ApiController]
[Route("api/admin")]
public class AdminController(AppDb db, Balancer balancer) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Overview()
    {
        var counts = await db.Tasks.GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count);

        var running = await db.Tasks.Where(t => t.Status == TaskState.Running && t.ServerId != null)
            .ToDictionaryAsync(t => t.ServerId!, t => t.Id);
        var done = await db.Tasks.Where(t => t.Status == TaskState.Success && t.ServerId != null)
            .GroupBy(t => t.ServerId!)
            .ToDictionaryAsync(g => g.Key, g => g.Count());

        var servers = balancer.Servers.Select(s => new
        {
            s.Id, s.Port, s.Pid, s.IsExtra, s.StartedAt,
            currentTaskId = running.TryGetValue(s.Id, out int taskId) ? taskId : (int?)null,
            tasksDone = done.GetValueOrDefault(s.Id),
            status = !s.Ready ? "Starting" : running.ContainsKey(s.Id) ? "Busy" : "Free"
        }).ToList();

        var tasks = await db.Tasks.AsNoTracking()
            .OrderByDescending(t => t.Id)
            .Take(50)
            .Select(t => new
            {
                t.Id, username = t.User!.Username, t.Status, t.Progress, t.ServerId, t.Cost, t.Error,
                t.CreatedAt, t.StartedAt, t.FinishedAt
            })
            .ToListAsync();

        return Ok(new
        {
            maxServers = Limits.MaxServers,
            maxQueue = Limits.MaxQueueLength,
            busyServers = servers.Count(s => s.status == "Busy"),
            queued = counts.GetValueOrDefault(TaskState.Queued),
            running = counts.GetValueOrDefault(TaskState.Running),
            success = counts.GetValueOrDefault(TaskState.Success),
            failed = counts.GetValueOrDefault(TaskState.Failed),
            cancelled = counts.GetValueOrDefault(TaskState.Cancelled),
            servers,
            tasks
        });
    }

    [HttpPost("tasks/{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id) => Ok(new { message = await TaskQueue.CancelAsync(db, id) });
}
