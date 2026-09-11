namespace TaskManager.Application.AI;

public sealed record AssistantToolParam(string Name, string Type, string Description, bool Required);

public sealed record AssistantToolSpec(
    string Name, string Description, bool IsMutating, IReadOnlyList<AssistantToolParam> Parameters);

/// <summary>
/// The complete, fixed whitelist of tools the AI Assistant may call. Nothing outside this list
/// is ever dispatched — <see cref="ToolDispatcher"/> rejects any tool name not present here
/// before it can reach a single MediatR command/query.
/// </summary>
public static class AssistantToolCatalog
{
    public static readonly IReadOnlyList<AssistantToolSpec> All = new[]
    {
        new AssistantToolSpec("GetTodayTasks", "List the user's tasks due today.", false,
            Array.Empty<AssistantToolParam>()),
        new AssistantToolSpec("GetOverdueTasks", "List the user's overdue tasks.", false,
            Array.Empty<AssistantToolParam>()),
        new AssistantToolSpec("GetTasks", "List the user's tasks, optionally filtered by status.", false,
            new[] { new AssistantToolParam("status", "string", "overdue | incomplete | completed", false) }),
        new AssistantToolSpec("GetTask", "Look up one task by (partial) title.", false,
            new[] { new AssistantToolParam("titleQuery", "string", "Words from the task title", true) }),
        new AssistantToolSpec("SearchTasks", "Search the user's tasks with a natural-language query.", false,
            new[] { new AssistantToolParam("query", "string", "The user's search phrase", true) }),

        new AssistantToolSpec("CreateTask", "Create a new task.", true, new[]
        {
            new AssistantToolParam("title", "string", "Task title", true),
            new AssistantToolParam("dueDate", "string", "YYYY-MM-DD", false),
            new AssistantToolParam("dueTime", "string", "HH:mm", false),
            new AssistantToolParam("priority", "string", "Low | Medium | High | Urgent", false),
            new AssistantToolParam("category", "string", "Category name", false),
        }),
        new AssistantToolSpec("UpdateTask", "Change a task's title or priority.", true, new[]
        {
            new AssistantToolParam("titleQuery", "string", "Words identifying the task", true),
            new AssistantToolParam("newTitle", "string", "New title", false),
            new AssistantToolParam("priority", "string", "Low | Medium | High | Urgent", false),
        }),
        new AssistantToolSpec("CompleteTask", "Mark a task complete.", true,
            new[] { new AssistantToolParam("titleQuery", "string", "Words identifying the task", true) }),
        new AssistantToolSpec("DeleteTask", "Delete a task.", true,
            new[] { new AssistantToolParam("titleQuery", "string", "Words identifying the task", true) }),
        new AssistantToolSpec("CreateSubtask", "Add a subtask to an existing task.", true, new[]
        {
            new AssistantToolParam("titleQuery", "string", "Words identifying the parent task", true),
            new AssistantToolParam("subtaskTitle", "string", "Subtask text", true),
        }),
        new AssistantToolSpec("CreateReminder", "Add a reminder to a task that has a due date and time.", true, new[]
        {
            new AssistantToolParam("titleQuery", "string", "Words identifying the task", true),
            new AssistantToolParam("offsetMinutes", "number", "Minutes before the due time", true),
        }),
        new AssistantToolSpec("RescheduleTask", "Move one task to a new due date.", true, new[]
        {
            new AssistantToolParam("titleQuery", "string", "Words identifying the task", true),
            new AssistantToolParam("newDueDate", "string", "YYYY-MM-DD", true),
        }),
        new AssistantToolSpec("RescheduleBulk", "Move a group of tasks (e.g. all overdue) to a new due date.", true, new[]
        {
            new AssistantToolParam("scope", "string", "overdue | low_priority", true),
            new AssistantToolParam("newDueDate", "string", "YYYY-MM-DD", true),
        }),
    };

    public static readonly IReadOnlyList<string> Names = All.Select(t => t.Name).ToList();

    public static AssistantToolSpec? Find(string name) => All.FirstOrDefault(t => t.Name == name);
}
