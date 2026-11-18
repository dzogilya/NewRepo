using HeatLab.Data;
using HeatLab.Solver;
using Microsoft.EntityFrameworkCore;

namespace HeatLab.Tests;

public class SolverTests
{
    [Fact]
    public async Task Центр_квадрата_з_одним_гарячим_краєм_має_25_відсотків_його_температури()
    {
        var p = new HeatParameters { Size = 41, Iterations = 50_000, Top = 100, Bottom = 0, Left = 0, Right = 0 };

        var r = await HeatSolver.SolveAsync(p, tolerance: 1e-7);

        Assert.True(r.Converged);
        double center = r.Map[(r.MapSize / 2) * r.MapSize + r.MapSize / 2];
        Assert.InRange(center, 24.5, 25.5);
    }

    [Fact]
    public async Task Однакові_краї_дають_однакову_температуру_всюди()
    {
        var p = new HeatParameters { Size = 30, Iterations = 20_000, Top = 50, Bottom = 50, Left = 50, Right = 50 };

        var r = await HeatSolver.SolveAsync(p);

        Assert.All(r.Map, v => Assert.Equal(50, v, 1));
    }

    [Fact]
    public async Task Скасування_зупиняє_розрахунок()
    {
        var p = new HeatParameters { Size = 500, Iterations = 50_000 };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HeatSolver.SolveAsync(p, onProgress: _ => throw new OperationCanceledException()));
    }
}

public class ValidationTests
{
    [Fact]
    public void Звичайна_задача_проходить_перевірку() =>
        Assert.Empty(new HeatParameters { Size = 200, Iterations = 5000 }.Validate());

    [Fact]
    public void Завелика_трудомісткість_відхиляється() =>
        Assert.Contains(new HeatParameters { Size = 1000, Iterations = 50_000 }.Validate(), e => e.Contains("завелика"));

    [Theory]
    [InlineData(5, 100)]
    [InlineData(2000, 100)]
    [InlineData(100, 1)]
    [InlineData(100, 100_000)]
    public void Розмір_і_ітерації_поза_межами_відхиляються(int size, int iterations) =>
        Assert.NotEmpty(new HeatParameters { Size = size, Iterations = iterations }.Validate());
}

public class BalancerTests
{
    private static readonly string[] Servers = ["server-1", "server-2"];

    [Fact]
    public void Обидва_вільні_задача_йде_на_перший() =>
        Assert.Equal("server-1", HeatLab.Gateway.Balancer.PickServer(Servers, []));

    [Fact]
    public void Перший_зайнятий_задача_йде_на_другий() =>
        Assert.Equal("server-2", HeatLab.Gateway.Balancer.PickServer(Servers, ["server-1"]));

    [Fact]
    public void Обидва_зайняті_вільного_сервера_немає() =>
        Assert.Null(HeatLab.Gateway.Balancer.PickServer(Servers, ["server-1", "server-2"]));
}

public class QueueTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"heatlab-test-{Guid.NewGuid():N}.sqlite");
    private string Cs => AppDb.ConnectionString(_file);

    [Fact]
    public async Task Скасування_задачі_в_черзі_відбувається_одразу()
    {
        await using var db = AppDb.Open(Cs);
        await AppDb.InitializeAsync(db);
        var task = new HeatTask { UserId = 1 };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();

        await TaskQueue.CancelAsync(db, task.Id);

        var status = await db.Tasks.Where(t => t.Id == task.Id).Select(t => t.Status).FirstAsync();
        Assert.Equal(TaskState.Cancelled, status);
    }

    [Fact]
    public async Task Задача_сервера_що_впав_повертається_в_чергу()
    {
        await using var db = AppDb.Open(Cs);
        await AppDb.InitializeAsync(db);
        db.Tasks.Add(new HeatTask { UserId = 1, Status = TaskState.Running, ServerId = "server-1" });
        db.Tasks.Add(new HeatTask { UserId = 1, Status = TaskState.Running, ServerId = "server-2" });
        await db.SaveChangesAsync();

        Assert.Equal(1, await TaskQueue.RequeueAsync(db, "server-1"));
        Assert.Equal(1, await db.Tasks.CountAsync(t => t.Status == TaskState.Queued && t.ServerId == null));
        Assert.Equal(1, await db.Tasks.CountAsync(t => t.Status == TaskState.Running && t.ServerId == "server-2"));
    }

    [Fact]
    public async Task Час_очікування_враховує_розмір_задач_і_прогрес()
    {
        await using var db = AppDb.Open(Cs);
        await AppDb.InitializeAsync(db);
        var start = DateTime.UtcNow.AddMinutes(-5);
        db.Tasks.Add(new HeatTask { UserId = 1, Status = TaskState.Success, Cost = 1_000_000_000, StartedAt = start, FinishedAt = start.AddSeconds(1) });
        db.Tasks.Add(new HeatTask { UserId = 1, Status = TaskState.Running, ServerId = "server-1", Cost = 10_000_000_000, Progress = 50 });
        var a = new HeatTask { UserId = 1, Cost = 2_000_000_000 };
        var b = new HeatTask { UserId = 1, Cost = 2_000_000_000 };
        db.Tasks.AddRange(a, b);
        await db.SaveChangesAsync();

        var queue = await TaskQueue.QueueInfoAsync(db, servers: 1);

        Assert.Equal((1, 5), queue[a.Id]);
        Assert.Equal((2, 7), queue[b.Id]);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            try { File.Delete(f); } catch { }
        }
    }
}
