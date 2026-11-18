using System.Security.Claims;
using System.Text.Json;
using HeatLab.Data;
using HeatLab.Solver;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HeatLab.Gateway;

[Authorize]
[ApiController]
[Route("api/tasks")]
public class TasksController(AppDb db, Balancer balancer) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsAdmin => User.IsInRole("admin");

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.UserId == UserId)
            .OrderByDescending(t => t.Id)
            .Take(Limits.MaxHistoryPerUser)
            .ToListAsync();
        var queue = await TaskQueue.QueueInfoAsync(db, Limits.MaxServers);

        return Ok(tasks.Select(t => new
        {
            t.Id, t.Status, t.Progress, t.Iteration, t.ServerId, t.Error, t.CreatedAt, t.StartedAt, t.FinishedAt,
            @params = JsonSerializer.Deserialize<HeatParameters>(t.Params, AppDb.Json),
            queuePosition = queue.TryGetValue(t.Id, out var q) ? q.Position : (int?)null,
            waitSeconds = queue.TryGetValue(t.Id, out q) ? q.WaitSeconds : (int?)null
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create(HeatParameters p)
    {
        var errors = p.Validate();
        if (errors.Count > 0) return UnprocessableEntity(new { message = string.Join(" ", errors) });

        int active = await db.Tasks.CountAsync(t => t.UserId == UserId &&
            (t.Status == TaskState.Queued || t.Status == TaskState.Running));
        if (active >= Limits.MaxActivePerUser)
            return StatusCode(429, new { message = $"У вас уже {active} активних задач (максимум {Limits.MaxActivePerUser}). Дочекайтесь завершення або скасуйте одну." });

        if (await db.Tasks.CountAsync(t => t.Status == TaskState.Queued) >= Limits.MaxQueueLength)
            return StatusCode(503, new { message = $"Відмова у виконанні: усі сервери зайняті, а черга заповнена ({Limits.MaxQueueLength} задачі). Спробуйте пізніше." });

        var old = await db.Tasks
            .Where(t => t.UserId == UserId && t.Status != TaskState.Queued && t.Status != TaskState.Running)
            .OrderByDescending(t => t.Id)
            .Skip(Limits.MaxHistoryPerUser - 1)
            .Select(t => t.Id)
            .ToListAsync();
        if (old.Count > 0) await db.Tasks.Where(t => old.Contains(t.Id)).ExecuteDeleteAsync();

        var task = new HeatTask
        {
            UserId = UserId,
            Params = JsonSerializer.Serialize(p, AppDb.Json),
            Cost = p.Cost
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();

        await balancer.DispatchAsync();

        var now = await db.Tasks.AsNoTracking().Where(t => t.Id == task.Id)
            .Select(t => new { t.Status, t.ServerId }).FirstAsync();
        if (now.Status != TaskState.Queued)
            return Ok(new { task.Id, message = $"Задачу #{task.Id} відправлено на {now.ServerId}." });

        var queue = await TaskQueue.QueueInfoAsync(db, Limits.MaxServers);
        var (position, wait) = queue.GetValueOrDefault(task.Id, (1, 10));
        string waitText = wait < 60 ? $"{wait} с" : $"{wait / 60} хв {wait % 60} с";
        return Ok(new
        {
            task.Id,
            message = $"Усі сервери зайняті — задачу #{task.Id} поставлено в чергу: позиція {position}, очікування ~{waitText}."
        });
    }

    [HttpGet("{id:int}/result")]
    public async Task<IActionResult> Result(int id)
    {
        var t = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
        if (t == null || (t.UserId != UserId && !IsAdmin)) return NotFound(new { message = "Задачу не знайдено." });
        if (t.Result == null) return NotFound(new { message = "Результат ще не готовий." });
        return Content(t.Result, "application/json");
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        if (!await CanAccessAsync(id)) return NotFound(new { message = "Задачу не знайдено." });
        return Ok(new { message = await TaskQueue.CancelAsync(db, id) });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await CanAccessAsync(id)) return NotFound(new { message = "Задачу не знайдено." });
        int deleted = await db.Tasks
            .Where(t => t.Id == id && t.Status != TaskState.Queued && t.Status != TaskState.Running)
            .ExecuteDeleteAsync();
        return deleted > 0 ? Ok() : Conflict(new { message = "Спочатку скасуйте задачу." });
    }

    private Task<bool> CanAccessAsync(int id) =>
        db.Tasks.AnyAsync(t => t.Id == id && (t.UserId == UserId || IsAdmin));
}
