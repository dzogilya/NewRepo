namespace Fractals.Core.Models;

public enum TaskStatus
{
    Queued,
    Running,
    Assembling,
    Success,
    Failed,
    Cancelled
}

public static class TaskStage
{
    public const string Queued = "В черзі";
    public const string Running = "Обчислення рядків";
    public const string Assembling = "Збирання зображення";
    public const string Success = "Завершено";
    public const string Cancelled = "Скасовано";
    public const string Failed = "Помилка";
}

