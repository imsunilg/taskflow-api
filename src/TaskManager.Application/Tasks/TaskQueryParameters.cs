namespace TaskManager.Application.Tasks;

public class TaskQueryParameters
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? Status { get; set; }
    public string? Priority { get; set; }
    public Guid? CategoryId { get; set; }
    public string? TagIds { get; set; }
    public string? DueDate { get; set; }
    public string? Search { get; set; }
    public string? Sort { get; set; }
    public string? GroupBy { get; set; }
}
