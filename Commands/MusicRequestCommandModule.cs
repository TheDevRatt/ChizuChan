using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace ChizuChan.Commands;

public class MusicRequestCommandModule : ApplicationCommandModule<ApplicationCommandContext>
{
    private readonly ILidarrService _lidarrService;
    private readonly IMusicSearchSessionService _sessionService;
    private readonly IMusicRequestAccessService _accessService;
    private readonly IYouTubeMusicSearchService _youtubeService;
    private readonly IMusicSearchEmbedBuilder _embedBuilder;
    private readonly IMusicRequestNotificationStore _notificationStore;
    private readonly LidarrOptions _options;
    private readonly ILogger<MusicRequestCommandModule> _logger;

    public MusicRequestCommandModule(
        ILidarrService lidarrService,
        IMusicSearchSessionService sessionService,
        IMusicRequestAccessService accessService,
        IYouTubeMusicSearchService youtubeService,
        IMusicSearchEmbedBuilder embedBuilder,
        IMusicRequestNotificationStore notificationStore,
        IOptions<LidarrOptions> options,
        ILogger<MusicRequestCommandModule> logger)
    {
        _lidarrService = lidarrService;
        _sessionService = sessionService;
        _accessService = accessService;
        _youtubeService = youtubeService;
        _embedBuilder = embedBuilder;
        _notificationStore = notificationStore;
        _options = options.Value;
        _logger = logger;
    }

    [SlashCommand(
        "music_search",
        "Search for requestable albums and matching YouTube tracks.",
        Contexts = [InteractionContextType.BotDMChannel])]
    public async Task SearchAsync(
        [SlashCommandParameter(Description = "Song, album, or artist to search for")]
        string query)
    {
        await RespondAsync(InteractionCallback.DeferredMessage());

        if (Context.Guild is not null)
        {
            await ModifyResponseAsync(m => m.Content = "This command can only be used in DMs.");
            return;
        }

        if (!await EnsureAccessAsync(MusicRequestOperation.Search))
            return;

        if (string.IsNullOrWhiteSpace(query))
        {
            await ModifyResponseAsync(m => m.Content = "Please enter a song, album, or artist to search for.");
            return;
        }

        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length > _options.MaxQueryLength)
        {
            await ModifyResponseAsync(m => m.Content = $"Search must be {_options.MaxQueryLength} characters or fewer.");
            return;
        }

        MusicSearchCommandResult search;
        using var timeoutSource = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Clamp(_options.SearchTimeoutSeconds, 5, 60)));
        try
        {
            search = await MusicSearchCommandCoordinator.SearchAsync(
                Context.User.Id,
                Context.Channel.Id,
                trimmedQuery,
                _sessionService,
                _lidarrService,
                _youtubeService,
                _logger,
                timeoutSource.Token);
        }
        catch (MusicSearchInProgressException)
        {
            await ModifyResponseAsync(m => m.Content = "Your previous music search is still running.");
            return;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            await ModifyResponseAsync(m => m.Content = "Music search timed out. Please try again.");
            return;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Music search command failed ({ExceptionType}).",
                exception.GetType().Name);
            await ModifyResponseAsync(m => m.Content = "Couldn't search for music right now.");
            return;
        }

        var rendered = _embedBuilder.Build(trimmedQuery, search.Snapshot);
        var responseMessage = await ModifyResponseAsync(message =>
        {
            message.Content = null;
            message.Embeds = [rendered.Embed];
            message.Components = rendered.Components;
        });

        if (!_sessionService.BindMessage(
                Context.User.Id,
                Context.Channel.Id,
                responseMessage.Id,
                search.Generation))
        {
            await ModifyResponseAsync(message =>
            {
                message.Content = "This music search was superseded by a newer search. Use the newer result instead.";
                message.Embeds = [];
                message.Components = [];
            });
        }
    }

    [SlashCommand(
        "music_request",
        "Request an album from your latest Lidarr search.",
        Contexts = [InteractionContextType.BotDMChannel])]
    public async Task RequestAsync(
        [SlashCommandParameter(Description = "Result number from your latest music search")]
        int result)
    {
        await RespondAsync(InteractionCallback.DeferredMessage());

        if (Context.Guild is not null)
        {
            await ModifyResponseAsync(m => m.Content = "This command can only be used in DMs.");
            return;
        }

        if (!await EnsureAccessAsync(MusicRequestOperation.Request))
            return;

        if (result is < 1 or > 10)
        {
            await ModifyResponseAsync(m => m.Content = "Result must be a number from 1 to 10.");
            return;
        }

        if (!_sessionService.TryGetResult(Context.User.Id, result, out var selectedAlbum))
        {
            await ModifyResponseAsync(m => m.Content = "That result isn't in your latest search. Run `/music_search` again.");
            return;
        }

        StandardResponse<LidarrAlbumRequestResultDTO> response;
        try
        {
            response = await _lidarrService.RequestAlbumAsync(selectedAlbum);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Lidarr music request command failed ({ExceptionType}).",
                exception.GetType().Name);
            await ModifyResponseAsync(m => m.Content = "Couldn't request that album right now.");
            return;
        }

        if (!response.Success || response.Data is null)
        {
            await ModifyResponseAsync(m => m.Content = "Couldn't request that album right now.");
            return;
        }

        var albumLabel = $"**{Limit(response.Data.ArtistName, 70)} — {Limit(response.Data.Title, 70)}**";
        var content = response.Data.AlreadyExists
            ? $"{albumLabel} is already in Lidarr."
            : $"Queued {albumLabel} in Lidarr.";

        if (response.Data.AlbumId <= 0 || string.IsNullOrWhiteSpace(response.Data.ForeignAlbumId))
        {
            content += " I couldn't register the completion alert, so please check Plex/Plexamp later.";
            await ModifyResponseAsync(m => m.Content = content);
            return;
        }

        try
        {
            await _notificationStore.AddOrGetAsync(new MusicRequestNotificationDTO
            {
                LidarrAlbumId = response.Data.AlbumId,
                ForeignAlbumId = response.Data.ForeignAlbumId,
                DiscordUserId = Context.User.Id,
                DmChannelId = Context.Channel.Id,
                ArtistName = response.Data.ArtistName,
                AlbumTitle = response.Data.Title,
                State = MusicRequestNotificationState.Pending,
            });
            content += " Chizu will DM you here when it's ready in Plex/Plexamp.";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Music completion subscription could not be persisted ({ExceptionType}).",
                exception.GetType().Name);
            content += " I couldn't register the completion alert, so please check Plex/Plexamp later.";
        }

        await ModifyResponseAsync(m => m.Content = content);
    }

    private async Task<bool> EnsureAccessAsync(MusicRequestOperation operation)
    {
        var access = _accessService.CheckAccess(Context.User.Id, operation);
        switch (access.Status)
        {
            case MusicRequestAccessStatus.Allowed:
                return true;
            case MusicRequestAccessStatus.Unauthorized:
                await ModifyResponseAsync(m => m.Content = "You don't have permission to use music requests.");
                return false;
            case MusicRequestAccessStatus.RateLimited:
                var seconds = Math.Max(1, (int)Math.Ceiling(access.RetryAfter.TotalSeconds));
                await ModifyResponseAsync(m => m.Content = $"Please wait {seconds}s before trying again.");
                return false;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";
}
