using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Voice;
using TaskManager.Domain.Enums;
using TaskManager.Infrastructure.NLP;
using Xunit;

namespace TaskManager.UnitTests;

public class VoiceTranscriptionHandlerTests
{
    private readonly ISpeechToTextService _stt = Substitute.For<ISpeechToTextService>();
    private readonly IVoiceRecordingRepository _recordings = Substitute.For<IVoiceRecordingRepository>();
    private readonly ICurrentUserService _currentUser = Substitute.For<ICurrentUserService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    private TranscribeAudioCommandHandler CreateSut() => new(
        _stt,
        new SimpleTaskFieldExtractor(),
        _recordings,
        _currentUser,
        _uow,
        NullLogger<TranscribeAudioCommandHandler>.Instance);

    public VoiceTranscriptionHandlerTests()
    {
        _currentUser.UserId.Returns(Guid.NewGuid());
    }

    private static Stream Audio(int bytes = 2048) => new MemoryStream(new byte[bytes]);

    [Fact]
    public async Task Uses_the_provider_transcript_verbatim_and_extracts_fields()
    {
        _stt.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new SpeechToTextResult("call the bank tomorrow at 3pm urgent", "Cloud"));

        var result = await CreateSut().Handle(
            new TranscribeAudioCommand(Audio(), "audio/webm", 2048, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RawText.Should().Be("call the bank tomorrow at 3pm urgent");
        result.Value.ExtractedFields.Priority.Should().Be(Priority.Urgent.ToString());
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Provider_failure_propagates_and_persists_nothing()
    {
        _stt.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns<SpeechToTextResult>(_ => throw new SpeechToTextException("provider unreachable"));

        var act = () => CreateSut().Handle(
            new TranscribeAudioCommand(Audio(), "audio/webm", 2048, null), CancellationToken.None);

        await act.Should().ThrowAsync<SpeechToTextException>();
        await _recordings.DidNotReceive().AddAsync(Arg.Any<Domain.Entities.VoiceRecordingEntity>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Empty_upload_is_rejected_before_calling_the_provider()
    {
        var result = await CreateSut().Handle(
            new TranscribeAudioCommand(Audio(0), "audio/webm", 0, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        await _stt.DidNotReceive().TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unsupported_mime_type_is_rejected()
    {
        var result = await CreateSut().Handle(
            new TranscribeAudioCommand(Audio(), "audio/ogg", 2048, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        await _stt.DidNotReceive().TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Empty_provider_transcript_is_reported_not_faked()
    {
        _stt.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new SpeechToTextResult("   ", "Cloud"));

        var result = await CreateSut().Handle(
            new TranscribeAudioCommand(Audio(), "audio/webm", 2048, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Message.ToLowerInvariant().Should().Contain("no speech");
    }

    [Fact]
    public async Task Text_reextract_path_needs_no_provider()
    {
        var result = await CreateSut().Handle(
            new TranscribeAudioCommand(null, null, 0, "buy milk tomorrow"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ExtractedFields.Title.Should().Be("Buy milk");
        await _stt.DidNotReceive().TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }
}
