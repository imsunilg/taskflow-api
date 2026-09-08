using FluentAssertions;
using FluentValidation;
using MediatR;
using TaskManager.Application.Behaviors;
using Xunit;
using ValidationException = TaskManager.Application.Common.ValidationException;

namespace TaskManager.UnitTests;

public class ValidationBehaviorTests
{
    public record DummyRequest(string Name) : IRequest<string>;

    public class DummyValidator : AbstractValidator<DummyRequest>
    {
        public DummyValidator() => RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required.");
    }

    [Fact]
    public async Task Short_circuits_before_the_handler_when_validation_fails()
    {
        var handlerCalled = false;
        var behavior = new ValidationBehavior<DummyRequest, string>(new[] { new DummyValidator() });

        var act = async () => await behavior.Handle(
            new DummyRequest(""),
            () => { handlerCalled = true; return Task.FromResult("ok"); },
            CancellationToken.None);

        (await act.Should().ThrowAsync<ValidationException>())
            .Which.Errors.Should().ContainSingle(e => e.Field == "Name");
        handlerCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Calls_the_handler_when_validation_passes()
    {
        var behavior = new ValidationBehavior<DummyRequest, string>(new[] { new DummyValidator() });

        var result = await behavior.Handle(
            new DummyRequest("valid"),
            () => Task.FromResult("handled"),
            CancellationToken.None);

        result.Should().Be("handled");
    }
}
