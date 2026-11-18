using System.Diagnostics;
using HeatLab.Data;
using Microsoft.EntityFrameworkCore;

namespace HeatLab.Gateway;

public class AppServer(int number)
{
    public int Number { get; } = number;
    public string Id => $"server-{Number}";
    public int Port => Limits.FirstServerPort + Number - 1;
    public string Url => $"http://localhost:{Port}";
    public bool IsExtra => Number > Limits.BaseServers;
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public Process Process { get; set; } = null!;
    public int Pid { get; set; }
    public bool Ready { get; set; }
    public DateTime? IdleSince { get; set; }
}

public class Balancer(ILogger<Balancer> log) : BackgroundService
{
    private static readonly TimeSpan ScaleDownAfter = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private AppServer[] _servers = [];

    public IReadOnlyList<AppServer> Servers => _servers;

    internal static string? PickServer(IEnumerable<string> ready, ICollection<string> busy) =>
        ready.FirstOrDefault(id => !busy.Contains(id));

    public async Task DispatchAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await using var db = AppDb.Open(AppDb.ConnectionString());
            var (queued, _) = await DispatchQueuedAsync(db);
            ScaleUp(queued);
        }
        finally { _lock.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await _lock.WaitAsync(stop);
        for (int n = 1; n <= Limits.BaseServers; n++) Launch(n);
        _lock.Release();

        while (true)
        {
            try { await Task.Delay(1000, stop); }
            catch (OperationCanceledException) { break; }

            await _lock.WaitAsync();
            try { await TickAsync(); }
            catch (Exception ex) { log.LogWarning("Балансувальник: {Message}", ex.Message); }
            finally { _lock.Release(); }
        }
    }

    private async Task TickAsync()
    {
        await using var db = AppDb.Open(AppDb.ConnectionString());

        foreach (var s in _servers.Where(s => s.Process.HasExited).ToList())
        {
            _servers = _servers.Where(x => x != s).ToArray();
            int back = await TaskQueue.RequeueAsync(db, s.Id);
            log.LogWarning("{Id} зупинився. Його задач повернуто в чергу: {Count}.", s.Id, back);
            if (!s.IsExtra) Launch(s.Number);
        }

        foreach (var s in _servers.Where(s => !s.Ready))
        {
            try
            {
                await Http.GetStringAsync($"{s.Url}/healthz");
                s.Ready = true;
                log.LogInformation("{Id} готовий приймати задачі.", s.Id);
            }
            catch (Exception) { }
        }

        var (queued, busy) = await DispatchQueuedAsync(db);
        ScaleUp(queued);
        ScaleDown(queued, busy);
    }

    private async Task<(int Queued, List<string> Busy)> DispatchQueuedAsync(AppDb db)
    {
        var busy = await db.Tasks
            .Where(t => t.Status == TaskState.Running && t.ServerId != null)
            .Select(t => t.ServerId!)
            .ToListAsync();
        var queue = await db.Tasks
            .Where(t => t.Status == TaskState.Queued)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync();

        int left = queue.Count;
        foreach (int id in queue)
        {
            string? target = PickServer(_servers.Where(s => s.Ready).Select(s => s.Id), busy);
            if (target == null) break;
            var server = _servers.First(s => s.Id == target);

            int claimed = await db.Tasks
                .Where(t => t.Id == id && t.Status == TaskState.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TaskState.Running)
                    .SetProperty(t => t.ServerId, target)
                    .SetProperty(t => t.Progress, 0)
                    .SetProperty(t => t.Iteration, 0)
                    .SetProperty(t => t.StartedAt, DateTime.UtcNow));
            if (claimed == 0)
            {
                left--;
                continue;
            }

            try
            {
                (await Http.PostAsync($"{server.Url}/run/{id}", null)).EnsureSuccessStatusCode();
                string skipped = string.Join(", ", busy);
                log.LogInformation("Задача #{Id} → {Server}{Why}", id, target,
                    skipped.Length > 0 ? $" (зайняті: {skipped})" : " (вільний)");
                busy.Add(target);
                left--;
            }
            catch (Exception)
            {
                await TaskQueue.RequeueAsync(db, target);
                server.Ready = false;
                log.LogWarning("{Id} не прийняв задачу #{Task}, шукаю інший сервер.", target, id);
            }
        }
        return (left, busy);
    }

    private void ScaleUp(int queued)
    {
        if (queued == 0 || _servers.Length >= Limits.MaxServers || _servers.Any(s => !s.Ready)) return;
        int next = Enumerable.Range(1, Limits.MaxServers).First(n => _servers.All(s => s.Number != n));
        log.LogWarning("МАСШТАБУВАННЯ ВГОРУ: усі {Count} сервери зайняті, у черзі {Queued}.", _servers.Length, queued);
        Launch(next);
    }

    private void ScaleDown(int queued, List<string> busy)
    {
        var now = DateTime.UtcNow;
        foreach (var s in _servers.Where(s => s.IsExtra && s.Ready).ToList())
        {
            if (queued > 0 || busy.Contains(s.Id))
            {
                s.IdleSince = null;
                continue;
            }
            s.IdleSince ??= now;
            if (now - s.IdleSince < ScaleDownAfter) continue;

            log.LogWarning("МАСШТАБУВАННЯ ВНИЗ: {Id} простоює {Sec} с — вимикаю.", s.Id, (int)ScaleDownAfter.TotalSeconds);
            try { s.Process.Kill(entireProcessTree: true); } catch { }
            _servers = _servers.Where(x => x != s).ToArray();
        }
    }

    private void Launch(int n)
    {
        var server = new AppServer(n);
        if (GatewayApp.IsPortBusy(server.Port))
        {
            log.LogError("Не можу запустити {Id}: порт {Port} зайнятий іншою програмою.", server.Id, server.Port);
            return;
        }

        string exe = Environment.ProcessPath!;
        string arguments = $"server {server.Id} {server.Port} {Environment.ProcessId}";
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments = $"\"{typeof(Balancer).Assembly.Location}\" {arguments}";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = AppContext.BaseDirectory
            }
        };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"   [{server.Id}] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"   [{server.Id}] {e.Data}"); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        server.Process = process;
        server.Pid = process.Id;
        _servers = _servers.Append(server).OrderBy(s => s.Number).ToArray();
        log.LogInformation("Запущено {Id} (порт {Port}, PID {Pid}).", server.Id, server.Port, server.Pid);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        foreach (var s in _servers)
        {
            try { s.Process.Kill(entireProcessTree: true); } catch { }
        }
    }
}
