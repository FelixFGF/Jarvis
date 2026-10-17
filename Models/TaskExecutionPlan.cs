namespace Jarvis.Models;

public sealed class TaskExecutionPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Status { get; set; } = "In progress";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public List<TaskExecutionPlanStep> Steps { get; set; } = [];
}

public sealed class TaskExecutionPlanStep
{
    public int Number { get; set; }
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string Detail { get; set; } = "";
}
