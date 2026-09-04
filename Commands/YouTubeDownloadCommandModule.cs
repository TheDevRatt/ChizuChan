using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace ChizuChan.Commands;

public sealed class YouTubeDownloadCommandModule : ApplicationCommandModule<ApplicationCommandContext>
{
    private readonly IMusicRequestAccessService _accessService;
    private readonly IYouTubeMusicActionHandler _handler;
    private readonly ILogger<YouTubeDownloadCommandModule> _logger;

    public YouTubeDownloadCommandModule(
        IMusicRequestAccessService accessService,
        IYouTubeMusicActionHandler handler,
        ILogger<YouTubeDownloadCommandModule> logger)
    {
        _accessService = accessService;
        _handler = handler;
        _logger = logger;
    }

    [SlashCommand(
        "youtube_download",
        "Download one authorized YouTube track from a video link.",
        Contexts = [InteractionContextType.BotDMChannel])]
    public async Task DownloadAsync(
        [SlashCommandParameter(Name = "url", Description = "HTTPS YouTube video URL")]
        string url)
    {
        await RespondAsync(InteractionCallback.DeferredMessage());

        YouTubeDownloadCommandResult result;
        if (Context.Guild is not null)
        {
            result = new(false, "This command can only be used in DMs.");
        }
        else
        {
            result = await YouTubeDownloadCommandCoordinator.ExecuteAsync(
                Context.User.Id,
                url,
                _accessService,
                _handler,
                _logger);
        }

        await ModifyResponseAsync(message =>
        {
            message.Content = result.Message;
            message.AllowedMentions = AllowedMentionsProperties.None;
        });
    }
}
