using System.Reflection;
using ChizuChan.Commands;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetCord;
using NetCord.Services.ApplicationCommands;

namespace ChizuChan.YouTubeDownload.Tests;

public sealed class YouTubeDownloadUrlParserTests
{
    [Theory]
    [InlineData("https://youtube.com/watch?v=abcdefghijk", "abcdefghijk")]
    [InlineData("https://www.youtube.com/watch?v=ABCDEFGHI_0", "ABCDEFGHI_0")]
    [InlineData("https://m.youtube.com/watch?v=abc-def_123", "abc-def_123")]
    [InlineData("https://music.youtube.com/watch?v=12345678901", "12345678901")]
    [InlineData("https://youtu.be/abcdefghijk", "abcdefghijk")]
    [InlineData("https://www.youtube.com/shorts/abcdefghijk", "abcdefghijk")]
    [InlineData("https://youtube.com/embed/abcdefghijk", "abcdefghijk")]
    [InlineData("  https://www.youtube.com/watch?si=opaque&v=abcdefghijk&t=30&list=PL123  ", "abcdefghijk")]
    [InlineData("https://youtu.be/abcdefghijk?si=opaque&t=30", "abcdefghijk")]
    [InlineData("https://music.youtube.com/shorts/abcdefghijk?feature=share", "abcdefghijk")]
    [InlineData("https://youtube.com/watch?v=abcdefghijk&irrelevant=valid%20escape", "abcdefghijk")]
    [InlineData("https://www.youtube.com:443/watch?v=abcdefghijk", "abcdefghijk")]
    public void TryParse_AcceptsSupportedExplicitVideoUrls(string input, string expectedId)
    {
        var parsed = YouTubeDownloadUrlParser.TryParse(input, out var videoId);

        Assert.True(parsed);
        Assert.Equal(expectedId, videoId);
    }

    public static TheoryData<string?> RejectedUrls => new()
    {
        null,
        "",
        "   ",
        "abcdefghijk",
        "http://www.youtube.com/watch?v=abcdefghijk",
        "ftp://www.youtube.com/watch?v=abcdefghijk",
        "https://user@youtube.com/watch?v=abcdefghijk",
        "https://youtube.com:444/watch?v=abcdefghijk",
        "https://youtube.com.evil.example/watch?v=abcdefghijk",
        "https://notyoutube.com/watch?v=abcdefghijk",
        "https://www.youtu.be/abcdefghijk",
        "https://youtube.com/playlist?list=PL123",
        "https://youtube.com/channel/abcdefghijk",
        "https://youtube.com/results?search_query=track",
        "https://youtube.com/watch?list=PL123",
        "https://youtube.com/watch?v=abcdefghijk&v=abcdefghijk",
        "https://youtube.com/watch?v=abcdefghijk&v=ABCDEFGHI_0",
        "https://youtube.com/watch?v=abcdefghijk#section",
        "https://youtube.com/watch?v=abcdefghijk#",
        "https://youtu.be/abcdefghijk/extra",
        "https://youtu.be/abcdefghijk/",
        "https://youtu.be/abcdefghijk?v=abcdefghijk",
        "https://youtube.com/shorts/abcdefghijk/extra",
        "https://youtube.com/embed/abcdefghijk?v=ABCDEFGHI_0",
        "https://youtube.com/watch?v=abcdefghij",
        "https://youtube.com/watch?v=abcdefghijkl",
        "https://youtube.com/watch?v=abcdefghij!",
        "https://youtube.com/watch?v=abcdefghij%6B",
        "https://youtube.com/watch%3Fv=abcdefghijk",
        "https://youtube.com/shorts%2Fabcdefghijk",
        "https://youtu.be%2Fabcdefghijk",
        "https://youtube.com/shorts/..%2Fabcdefghijk",
        "https://youtube.com/shorts/../watch?v=abcdefghijk",
        "https://youtube.com\\@evil.example/watch?v=abcdefghijk",
        "https://youtube.com/watch?V=abcdefghijk",
        "https://youtube.com//watch?v=abcdefghijk",
        "https://youtube.com/watch/?v=abcdefghijk",
        "https://www.yоutube.com/watch?v=abcdefghijk",
        "https://ｙoutube.com/watch?v=abcdefghijk",
    };

    [Theory]
    [MemberData(nameof(RejectedUrls))]
    public void TryParse_RejectsAmbiguousOrUnsafeUrls(string? input)
    {
        Assert.False(YouTubeDownloadUrlParser.TryParse(input, out var videoId));
        Assert.Equal(string.Empty, videoId);
    }

    [Fact]
    public void TryParse_RejectsEmptyExplicitPort()
    {
        const string input = "https://youtube.com:/watch?v=abcdefghijk";

        Assert.False(YouTubeDownloadUrlParser.TryParse(input, out var videoId));
        Assert.Equal(string.Empty, videoId);
    }

    [Theory]
    [InlineData("https://youtube.com/watch?v=abcdefghijk&irrelevant=%ZZ")]
    [InlineData("https://youtube.com/watch?v=abcdefghijk&irrelevant=%")]
    [InlineData("https://youtube.com/watch?v=abcdefghijk&irrelevant=%2G")]
    public void TryParse_RejectsMalformedPercentEscapesInRawQuery(string input)
    {
        Assert.False(YouTubeDownloadUrlParser.TryParse(input, out var videoId));
        Assert.Equal(string.Empty, videoId);
    }

    [Fact]
    public void TryParse_RejectsNewlineAndTabWrappedUrl()
    {
        const string input = "\n\thttps://youtube.com/watch?v=abcdefghijk\t\n";

        Assert.False(YouTubeDownloadUrlParser.TryParse(input, out var videoId));
        Assert.Equal(string.Empty, videoId);
    }

    [Fact]
    public void TryParse_RejectsOverlongInput()
    {
        var input = "https://youtube.com/watch?v=abcdefghijk&padding=" + new string('a', 2048);

        Assert.False(YouTubeDownloadUrlParser.TryParse(input, out var videoId));
        Assert.Equal(string.Empty, videoId);
    }

    [Fact]
    public void TryParse_RejectsInputWhoseOuterWhitespaceExceedsTheBound()
    {
        var input = new string(' ', YouTubeDownloadUrlParser.MaximumUrlLength) +
                    "https://youtube.com/watch?v=abcdefghijk";

        Assert.False(YouTubeDownloadUrlParser.TryParse(input, out var videoId));
        Assert.Equal(string.Empty, videoId);
    }
}

public sealed class YouTubeDownloadCommandCoordinatorTests
{
    private const ulong UserId = 42;
    private const string RawUrl = "https://www.youtube.com/watch?v=abcdefghijk&t=30&si=user-input";

    [Fact]
    public async Task ExecuteAsync_UnauthorizedUserNeverCallsHandler()
    {
        var access = AccessReturning(MusicRequestAccessResult.Unauthorized());
        var handler = new Mock<IYouTubeMusicActionHandler>(MockBehavior.Strict);

        var result = await YouTubeDownloadCommandCoordinator.ExecuteAsync(
            UserId, RawUrl, access.Object, handler.Object, NullLogger.Instance);

        Assert.False(result.Success);
        Assert.Equal("You don't have permission to use music requests.", result.Message);
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_RateLimitedUserNeverCallsHandler()
    {
        var access = AccessReturning(MusicRequestAccessResult.RateLimited(TimeSpan.FromMilliseconds(1200)));
        var handler = new Mock<IYouTubeMusicActionHandler>(MockBehavior.Strict);

        var result = await YouTubeDownloadCommandCoordinator.ExecuteAsync(
            UserId, RawUrl, access.Object, handler.Object, NullLogger.Instance);

        Assert.False(result.Success);
        Assert.Equal("Please wait 2s before trying again.", result.Message);
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ValidUrlPassesOnlyCanonicalIdAndPropagatesHandlerResult()
    {
        var access = AccessReturning(MusicRequestAccessResult.Allowed());
        var expected = YouTubeMusicActionResult.Succeeded("Downloaded an authorized track.");
        var handler = new Mock<IYouTubeMusicActionHandler>(MockBehavior.Strict);
        handler.Setup(service => service.HandleAsync(UserId, "abcdefghijk", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await YouTubeDownloadCommandCoordinator.ExecuteAsync(
            UserId, RawUrl, access.Object, handler.Object, NullLogger.Instance);

        Assert.Equal(new YouTubeDownloadCommandResult(expected.Success, expected.Message), result);
        handler.Verify(service => service.HandleAsync(
            UserId, "abcdefghijk", It.IsAny<CancellationToken>()), Times.Once);
        handler.Verify(service => service.HandleAsync(
            It.IsAny<ulong>(), It.Is<string>(value => value.Contains("youtube", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()), Times.Never);
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_InvalidUrlNeverCallsHandler()
    {
        var access = AccessReturning(MusicRequestAccessResult.Allowed());
        var handler = new Mock<IYouTubeMusicActionHandler>(MockBehavior.Strict);

        var result = await YouTubeDownloadCommandCoordinator.ExecuteAsync(
            UserId, "https://youtube.com/playlist?list=PL123", access.Object, handler.Object, NullLogger.Instance);

        Assert.False(result.Success);
        Assert.Equal("Please provide a supported HTTPS YouTube video URL.", result.Message);
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsExactCancellationTokenToHandler()
    {
        var access = AccessReturning(MusicRequestAccessResult.Allowed());
        using var cancellation = new CancellationTokenSource();
        var handler = new Mock<IYouTubeMusicActionHandler>(MockBehavior.Strict);
        handler.Setup(service => service.HandleAsync(UserId, "abcdefghijk", cancellation.Token))
            .ReturnsAsync(YouTubeMusicActionResult.Succeeded("Downloaded."));

        await YouTubeDownloadCommandCoordinator.ExecuteAsync(
            UserId, RawUrl, access.Object, handler.Object, NullLogger.Instance, cancellation.Token);

        handler.Verify(service => service.HandleAsync(
            UserId, "abcdefghijk", cancellation.Token), Times.Once);
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_RethrowsHandlerCancellationWhenCallerTokenIsCanceled()
    {
        var access = AccessReturning(MusicRequestAccessResult.Allowed());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new Mock<IYouTubeMusicActionHandler>(MockBehavior.Strict);
        handler.Setup(service => service.HandleAsync(UserId, "abcdefghijk", cancellation.Token))
            .Returns(Task.FromCanceled<YouTubeMusicActionResult>(cancellation.Token));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            YouTubeDownloadCommandCoordinator.ExecuteAsync(
                UserId, RawUrl, access.Object, handler.Object, NullLogger.Instance, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesOrdinaryHandlerFailureResult()
    {
        var access = AccessReturning(MusicRequestAccessResult.Allowed());
        var expected = YouTubeMusicActionResult.Failed("The authorized track was not available.");
        var handler = new Mock<IYouTubeMusicActionHandler>(MockBehavior.Strict);
        handler.Setup(service => service.HandleAsync(UserId, "abcdefghijk", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await YouTubeDownloadCommandCoordinator.ExecuteAsync(
            UserId, RawUrl, access.Object, handler.Object, NullLogger.Instance);

        Assert.Equal(expected.Success, result.Success);
        Assert.Equal(expected.Message, result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_BoundsOverlongHandlerMessageWithoutExposingRawUrl()
    {
        var access = AccessReturning(MusicRequestAccessResult.Allowed());
        var handlerMessage = new string('x', 501) + RawUrl;
        var handler = new Mock<IYouTubeMusicActionHandler>(MockBehavior.Strict);
        handler.Setup(service => service.HandleAsync(UserId, "abcdefghijk", It.IsAny<CancellationToken>()))
            .ReturnsAsync(YouTubeMusicActionResult.Failed(handlerMessage));

        var result = await YouTubeDownloadCommandCoordinator.ExecuteAsync(
            UserId, RawUrl, access.Object, handler.Object, NullLogger.Instance);

        Assert.False(result.Success);
        Assert.Equal(500, result.Message.Length);
        Assert.DoesNotContain(RawUrl, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_HandlerFailureIsBoundedAndDoesNotExposeRawUrl()
    {
        var access = AccessReturning(MusicRequestAccessResult.Allowed());
        var handler = new Mock<IYouTubeMusicActionHandler>(MockBehavior.Strict);
        handler.Setup(service => service.HandleAsync(UserId, "abcdefghijk", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("secret path " + RawUrl));

        var result = await YouTubeDownloadCommandCoordinator.ExecuteAsync(
            UserId, RawUrl, access.Object, handler.Object, NullLogger.Instance);

        Assert.False(result.Success);
        Assert.Equal("Couldn't download that YouTube track right now.", result.Message);
        Assert.DoesNotContain(RawUrl, result.Message, StringComparison.Ordinal);
        Assert.InRange(result.Message.Length, 1, 500);
    }

    private static Mock<IMusicRequestAccessService> AccessReturning(MusicRequestAccessResult result)
    {
        var access = new Mock<IMusicRequestAccessService>(MockBehavior.Strict);
        access.Setup(service => service.CheckAccess(UserId, MusicRequestOperation.Download)).Returns(result);
        return access;
    }
}

public sealed class YouTubeDownloadCommandContractTests
{
    [Fact]
    public void Command_HasExpectedNameDescriptionDmContextAndRequiredUrlParameter()
    {
        var method = typeof(YouTubeDownloadCommandModule).GetMethod(nameof(YouTubeDownloadCommandModule.DownloadAsync));
        var command = Assert.IsType<SlashCommandAttribute>(Assert.Single(
            method!.GetCustomAttributes(typeof(SlashCommandAttribute), inherit: false)));

        Assert.Equal("youtube_download", command.Name);
        Assert.Equal([InteractionContextType.BotDMChannel], command.Contexts);
        Assert.Contains("authorized", command.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("YouTube", command.Description, StringComparison.OrdinalIgnoreCase);

        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal(typeof(string), parameter.ParameterType);
        Assert.False(parameter.HasDefaultValue);
        var parameterAttribute = Assert.IsType<SlashCommandParameterAttribute>(Assert.Single(
            parameter.GetCustomAttributes(typeof(SlashCommandParameterAttribute), inherit: false)));
        Assert.Equal("url", parameterAttribute.Name);
    }

    [Fact]
    public void Constructor_RequiresAccessHandlerAndLogger()
    {
        var constructor = Assert.Single(typeof(YouTubeDownloadCommandModule).GetConstructors());
        var parameterTypes = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Equal(
            [
                typeof(IMusicRequestAccessService),
                typeof(IYouTubeMusicActionHandler),
                typeof(Microsoft.Extensions.Logging.ILogger<YouTubeDownloadCommandModule>),
            ],
            parameterTypes);
    }
}
