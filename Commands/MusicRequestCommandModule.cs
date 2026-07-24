using System.Text;
using ChizuChan.DTOs;
using ChizuChan.Options;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace ChizuChan.Commands;

public class MusicRequestCommandModule : ApplicationCommandModule<ApplicationCommandContext>
{
    private readonly ILidarrService _lidarrService;
    private readonly IMusicSearchSessionService _sessionService;
    private readonly IMusicRequestAccessService _accessService;
    private readonly LidarrOptions _options;

    public MusicRequestCommandModule(
        ILidarrService lidarrService,
        IMusicSearchSessionService sessionService,
        IMusicRequestAccessService accessService,
        IOptions<LidarrOptions> options)
    {
        _lidarrService = lidarrService;
        _sessionService = sessionService;
        _accessService = accessService;
        _options = options.Value;
    }

    [SlashCommand(
        "music_search",
        "Search Lidarr for an album.",
        Contexts = [InteractionContextType.BotDMChannel])]
    public async Task SearchAsync(
        [SlashCommandParameter(Description = "Album title or artist to search for")]
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
            await ModifyResponseAsync(m => m.Content = "Please enter an album or artist to search for.");
            return;
        }

        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length > _options.MaxQueryLength)
        {
            await ModifyResponseAsync(m => m.Content = $"Search must be {_options.MaxQueryLength} characters or fewer.");
            return;
        }

        StandardResponse<IReadOnlyList<LidarrAlbumDTO>> response;
        try
        {
            response = await MusicSearchCommandCoordinator.SearchAsync(
                Context.User.Id,
                trimmedQuery,
                _sessionService,
                _lidarrService);
        }
        catch
        {
            await ModifyResponseAsync(m => m.Content = "Couldn't search for albums right now.");
            return;
        }

        if (!response.Success)
        {
            await ModifyResponseAsync(m => m.Content = "Couldn't search for albums right now.");
            return;
        }

        var albums = response.Data?.Take(10).ToArray() ?? [];
        if (albums.Length == 0)
        {
            await ModifyResponseAsync(m => m.Content = "No albums found.");
            return;
        }

        _sessionService.SaveResults(Context.User.Id, albums);

        var message = new StringBuilder("**Album results**\n");
        for (var index = 0; index < albums.Length; index++)
        {
            var album = albums[index];
            var artist = Limit(album.Artist?.ArtistName ?? "Unknown artist", 70);
            var title = Limit(album.Title ?? "Unknown album", 70);
            var year = album.ReleaseDate?.Year.ToString() ?? "year unknown";
            message.Append(index + 1)
                .Append(". ")
                .Append(artist)
                .Append(" — ")
                .Append(title)
                .Append(" (")
                .Append(year)
                .Append(")\n");
        }

        message.Append("Use `/music_request result:<number>` to request one.");
        await ModifyResponseAsync(m => m.Content = message.ToString());
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
        catch
        {
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
