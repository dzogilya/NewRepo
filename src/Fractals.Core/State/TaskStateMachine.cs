using Fractals.Core.Models;
using TaskStatus = Fractals.Core.Models.TaskStatus;

namespace Fractals.Core.State;

public class TaskStateMachine
{
    private static readonly Dictionary<TaskStatus, HashSet<TaskStatus>> AllowedTransitions = new()
    {
        [TaskStatus.Queued] = [TaskStatus.Running, TaskStatus.Cancelled],
        [TaskStatus.Running] = [TaskStatus.Assembling, TaskStatus.Cancelled, TaskStatus.Failed, TaskStatus.Queued],
        [TaskStatus.Assembling] = [TaskStatus.Success, TaskStatus.Failed, TaskStatus.Cancelled],
        [TaskStatus.Success] = [],
        [TaskStatus.Failed] = [],
        [TaskStatus.Cancelled] = []
    };

    public static bool CanTransition(TaskStatus current, TaskStatus next)
    {
        return AllowedTransitions.TryGetValue(current, out var allowed) && allowed.Contains(next);
    }

    public static void ValidateTransition(TaskStatus current, TaskStatus next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException(
                $"Неприпустимий перехід стану задачі з '{current}' до '{next}'.");
        }
    }

    public static bool IsTerminal(TaskStatus status) =>
        status is TaskStatus.Success or TaskStatus.Failed or TaskStatus.Cancelled;
}

