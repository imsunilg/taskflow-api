using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TaskManager.Application.AI;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;

namespace TaskManager.UnitTests;

public class AiUsageLoggingBehaviorTests
{
    private sealed record FakeAiRequest(string AiRequestType = "fake") : IRequest<string>, IAiRequest;
    private sealed record NonAiRequest : IRequest<string>;

    private static AiUsageLoggingBehavior<TRequest, string> MakeBehavior<TRequest>(
        ICurrentUserService currentUser, IAiRepository aiRepo, IUnitOfWork uow, AiCallContext callContext, int dailyCap)
        where TRequest : notnull =>
        new(currentUser, aiRepo, uow, callContext,
            Options.Create(new AiQuotaOptions { DailyRequestCap = dailyCap }),
            NullLogger<AiUsageLoggingBehavior<TRequest, string>>.Instance);

    [Fact]
    public async Task Non_AI_requests_pass_through_untouched_with_no_quota_check()
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        var aiRepo = Substitute.For<IAiRepository>();
        var uow = Substitute.For<IUnitOfWork>();
        var behavior = MakeBehavior<NonAiRequest>(currentUser, aiRepo, uow, new AiCallContext(), dailyCap: 1);

        var result = await behavior.Handle(new NonAiRequest(), () => Task.FromResult("ok"), CancellationToken.None);

        result.Should().Be("ok");
        await aiRepo.DidNotReceive().CountUsageSinceAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Once_the_daily_cap_is_reached_the_request_is_rejected_before_the_handler_runs()
    {
        var userId = Guid.NewGuid();
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(userId);
        var aiRepo = Substitute.For<IAiRepository>();
        aiRepo.CountUsageSinceAsync(userId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(5);
        var uow = Substitute.For<IUnitOfWork>();
        var behavior = MakeBehavior<FakeAiRequest>(currentUser, aiRepo, uow, new AiCallContext(), dailyCap: 5);

        var handlerCalled = false;
        Func<Task> act = () => behavior.Handle(new FakeAiRequest(), () => { handlerCalled = true; return Task.FromResult("ok"); }, CancellationToken.None);

        await act.Should().ThrowAsync<AIQuotaExceededException>();
        handlerCalled.Should().BeFalse("the handler must never run once the quota is exceeded");
    }

    [Fact]
    public async Task Under_the_cap_the_handler_runs_and_usage_is_recorded_when_the_AI_service_reports_a_call()
    {
        var userId = Guid.NewGuid();
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(userId);
        var aiRepo = Substitute.For<IAiRepository>();
        aiRepo.CountUsageSinceAsync(userId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(3);
        var uow = Substitute.For<IUnitOfWork>();
        var callContext = new AiCallContext();
        callContext.Record("gemini-2.5-flash", 42, 17);
        var behavior = MakeBehavior<FakeAiRequest>(currentUser, aiRepo, uow, callContext, dailyCap: 5);

        var result = await behavior.Handle(new FakeAiRequest(), () => Task.FromResult("ok"), CancellationToken.None);

        result.Should().Be("ok");
        await aiRepo.Received(1).AddUsageAsync(
            Arg.Is<AiUsageEntity>(u => u.UserId == userId && u.Model == "gemini-2.5-flash"
                && u.InputTokens == 42 && u.OutputTokens == 17 && u.RequestType == "fake"),
            Arg.Any<CancellationToken>());
        await uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Zero_means_unlimited_and_never_calls_the_repository_count()
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(Guid.NewGuid());
        var aiRepo = Substitute.For<IAiRepository>();
        var uow = Substitute.For<IUnitOfWork>();
        var behavior = MakeBehavior<FakeAiRequest>(currentUser, aiRepo, uow, new AiCallContext(), dailyCap: 0);

        var result = await behavior.Handle(new FakeAiRequest(), () => Task.FromResult("ok"), CancellationToken.None);

        result.Should().Be("ok");
        await aiRepo.DidNotReceive().CountUsageSinceAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }
}
