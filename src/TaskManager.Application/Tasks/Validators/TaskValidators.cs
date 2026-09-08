using FluentValidation;
using TaskManager.Application.Tasks.Commands;

namespace TaskManager.Application.Tasks.Validators;

public class CreateTaskCommandValidator : AbstractValidator<CreateTaskCommand>
{
    public CreateTaskCommandValidator()
    {
        RuleFor(x => x.Request.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(500);

        RuleFor(x => x.Request.Description)
            .MaximumLength(5000);

        RuleFor(x => x.Request)
            .Must(r => !r.DueTime.HasValue || r.DueDate.HasValue)
            .WithName("dueTime")
            .WithMessage("Due time requires a due date.");

        RuleFor(x => x.Request)
            .Must(r => !r.ReminderOffsetMinutes.HasValue || (r.DueDate.HasValue && r.DueTime.HasValue))
            .WithName("reminderOffsetMinutes")
            .WithMessage("A reminder requires both a due date and a due time.");
    }
}

public class UpdateTaskCommandValidator : AbstractValidator<UpdateTaskCommand>
{
    public UpdateTaskCommandValidator()
    {
        RuleFor(x => x.Request.Title)
            .MaximumLength(500)
            .When(x => x.Request.Title is not null);

        RuleFor(x => x.Request.Description)
            .MaximumLength(5000)
            .When(x => x.Request.Description is not null);

        RuleFor(x => x.Request)
            .Must(r => !r.DueTime.HasValue || r.DueDate.HasValue)
            .WithName("dueTime")
            .WithMessage("Due time requires a due date.");
    }
}

public class CreateSubtaskCommandValidator : AbstractValidator<CreateSubtaskCommand>
{
    public CreateSubtaskCommandValidator()
    {
        RuleFor(x => x.Request.Text).NotEmpty().MaximumLength(500);
    }
}
