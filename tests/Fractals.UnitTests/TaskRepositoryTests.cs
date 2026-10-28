using FluentAssertions;
using Fractals.Data;
using Fractals.Data.Entities;
using Fractals.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Fractals.UnitTests;

public class TaskRepositoryTests
{
    private static FractalsDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<FractalsDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new FractalsDbContext(options);
    }

    [Fact]
    public async Task ClaimNextTaskAsync_ClaimsQueuedTask()
    {
        using var context = CreateInMemoryContext();
        var repo = new TaskRepository(context);

        var task = new TaskEntity
        {
            Type = "Mandelbrot",
            Status = "Queued",
            Cost = 1000,
            CreatedAt = DateTimeOffset.UtcNow
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var claimed = await repo.ClaimNextTaskAsync("app-server-1");

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(task.Id);
        claimed.Status.Should().Be("Running");
        claimed.AppServerId.Should().Be("app-server-1");
        claimed.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task ClaimNextTaskAsync_SkipsCanceledTasks()
    {
        using var context = CreateInMemoryContext();
        var repo = new TaskRepository(context);

        var task = new TaskEntity
        {
            Type = "Mandelbrot",
            Status = "Queued",
            IsCanceled = true,
            Cost = 1000,
            CreatedAt = DateTimeOffset.UtcNow
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var claimed = await repo.ClaimNextTaskAsync("app-server-1");
        claimed.Should().BeNull();
    }

    [Fact]
    public async Task CancelTaskAsync_QueuedTask_MarksCancelled()
    {
        using var context = CreateInMemoryContext();
        var repo = new TaskRepository(context);

        var task = new TaskEntity
        {
            Type = "Mandelbrot",
            Status = "Queued",
            CreatedAt = DateTimeOffset.UtcNow
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        bool canceled = await repo.CancelTaskAsync(task.Id);

        canceled.Should().BeTrue();
        var updated = await context.Tasks.FindAsync(task.Id);
        updated!.Status.Should().Be("Cancelled");
        updated.IsCanceled.Should().BeTrue();
    }

    [Fact]
    public async Task QueuePositionAndCost_CalculatedCorrectly()
    {
        using var context = CreateInMemoryContext();
        var repo = new TaskRepository(context);

        var now = DateTimeOffset.UtcNow;
        var task1 = new TaskEntity { Status = "Queued", Cost = 500, CreatedAt = now.AddSeconds(-10) };
        var task2 = new TaskEntity { Status = "Queued", Cost = 700, CreatedAt = now.AddSeconds(-5) };
        var task3 = new TaskEntity { Status = "Queued", Cost = 900, CreatedAt = now };

        context.Tasks.AddRange(task1, task2, task3);
        await context.SaveChangesAsync();

        int pos3 = await repo.GetQueuePositionAsync(task3.Id);
        long costBefore3 = await repo.GetQueuePendingCostAsync(task3.Id);

        pos3.Should().Be(3);
        costBefore3.Should().Be(1200); // 500 + 700
    }
}
