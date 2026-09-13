using System.Reflection;
using ChizuChan.Commands;
using ChizuChan.Commands.Controllers;
using ChizuChan.DTOs;
using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;
using static ChizuChan.LongMedia.Delivery.Tests.DurableDeliveryTests;

namespace ChizuChan.LongMedia.Delivery.Tests;

public class DeliveryEntryPointTests
{
    [Fact]
    public async Task Production_scan_resolves_both_entry_points_to_one_hosted_delivery_adapter()
    {
        await using var f = new Fixture();
        var direct = ActivatorUtilities.CreateInstance<YouTubeDownloadCommandModule>(f.Provider);
        Assert.Same(f.Handler, typeof(YouTubeDownloadCommandModule).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(direct));
        var search = f.Provider.GetRequiredService<IMusicSearchActionCoordinator>();
        Assert.Same(f.Handler, search.GetType().GetField("_youTubeHandler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(search));
        Assert.Same(f.Handler, Assert.Single(f.Provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()));
        Assert.Single(f.Provider.GetServices<IYouTubeMusicActionHandler>());
    }

    [Fact]
    public async Task Search_acknowledges_once_then_completes_after_interaction_expiry()
    {
        await using var f = new Fixture();
        var (coordinator, token) = Search(f);
        await f.Start();
        var interaction = new ExpiringInteraction();
        var result = await coordinator.ExecuteAndCommitAsync(42, 12, 34, token.Segment, 0,
            interaction.Acknowledge, interaction.Commit).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Success);
        Assert.Contains("job", result.Message);
        Assert.Equal(1, interaction.Acknowledgments);
        Assert.Equal(1, interaction.Commits);
        await f.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        interaction.Advance(TimeSpan.FromMinutes(16));
        f.Engine.Complete.TrySetResult(YouTubeMusicActionResult.Succeeded("Imported; Plex refresh pending."));
        await f.Until(() => f.Sends.Count == 1);
        Assert.Equal(1, interaction.Commits);
        Assert.Equal(42ul, f.Sends.Single().User);
    }

    [Theory]
    [InlineData(99ul, 12ul, 34ul, false, 0)]
    [InlineData(42ul, 99ul, 34ul, false, 0)]
    [InlineData(42ul, 12ul, 99ul, false, 0)]
    [InlineData(42ul, 12ul, 34ul, true, 0)]
    [InlineData(42ul, 12ul, 34ul, false, 1)]
    public async Task Search_rejects_wrong_owner_channel_message_token_or_index(ulong user, ulong channel, ulong message, bool wrongToken, int index)
    {
        await using var f = new Fixture();
        var (coordinator, token) = Search(f);
        var result = await coordinator.ExecuteAsync(user, channel, message, wrongToken ? "stale" : token.Segment, index);
        Assert.False(result.Success);
        Assert.Empty(f.Jobs());
        Assert.Equal(0, f.Engine.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_preserves_download_access_and_rate_limit(bool rateLimited)
    {
        await using var f = new Fixture();
        f.Access.Setup(x => x.CheckAccess(42, MusicRequestOperation.Download)).Returns(rateLimited
            ? MusicRequestAccessResult.RateLimited(TimeSpan.FromSeconds(1)) : MusicRequestAccessResult.Unauthorized());
        var (coordinator, token) = Search(f);
        Assert.False((await coordinator.ExecuteAsync(42, 12, 34, token.Segment, 0)).Success);
        Assert.Empty(f.Jobs());
    }

    [Fact]
    public async Task Search_claim_survives_ack_yield_and_duplicate_retries_join_direct_job()
    {
        await using var f = new Fixture();
        var (coordinator, token) = Search(f);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.ExecuteAndCommitAsync(42, 12, 34, token.Segment, 0,
            _ => release.Task, (_, _) => Task.CompletedTask);
        var racing = await coordinator.ExecuteAsync(42, 12, 34, token.Segment, 0);
        Assert.False(racing.Success);
        Assert.Contains("already", racing.Message);
        release.SetResult();
        var firstResult = await first;
        var retry = await coordinator.ExecuteAsync(42, 12, 34, token.Segment, 0);
        var direct = await YouTubeDownloadCommandCoordinator.ExecuteAsync(42, "https://youtu.be/abcdefghijk",
            f.Access.Object, f.Handler, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Equal(firstResult.Message, retry.Message);
        Assert.Equal(firstResult.Message, direct.Message);
        Assert.Single(f.Jobs());
    }

    [Fact]
    public async Task Lost_search_commit_does_not_lose_or_duplicate_durable_admission()
    {
        await using var f = new Fixture();
        var (coordinator, token) = Search(f);
        await Assert.ThrowsAsync<IOException>(() => coordinator.ExecuteAndCommitAsync(42, 12, 34, token.Segment, 0,
            _ => Task.CompletedTask, (_, _) => throw new IOException("interaction expired")));
        Assert.Single(f.Jobs());
        Assert.True((await coordinator.ExecuteAsync(42, 12, 34, token.Segment, 0)).Success);
        Assert.Single(f.Jobs());
    }

    [Fact]
    public void Owner_status_is_a_discovered_DM_command()
    {
        var type = typeof(Program).Assembly.GetType("ChizuChan.Commands.YouTubeLongMediaStatusModule");
        Assert.NotNull(type);
        Assert.True(typeof(ApplicationCommandModule<ApplicationCommandContext>).IsAssignableFrom(type));
        var method = type.GetMethod("StatusAsync");
        Assert.NotNull(method);
        var command = method.GetCustomAttribute<SlashCommandAttribute>();
        Assert.NotNull(command);
        Assert.Equal("youtube_job", command.Name);
        Assert.Equal([NetCord.InteractionContextType.BotDMChannel], command.Contexts);
    }

    [Fact]
    public async Task Status_lookup_is_owner_only_and_includes_delivery_state()
    {
        await using var f = new Fixture();
        await f.Handler.HandleAsync(42, "abcdefghijk");
        var id = f.Jobs().Single()["Id"]!.GetValue<string>();
        var type = typeof(Program).Assembly.GetType("ChizuChan.Commands.YouTubeLongMediaStatusModule");
        Assert.NotNull(type);
        var method = type.GetMethod("FormatStatus");
        Assert.NotNull(method);
        var store = f.Provider.GetRequiredService<YouTubeLongMediaStore>();
        var own = (string)method.Invoke(null, [42ul, id, store])!;
        var other = (string)method.Invoke(null, [99ul, id, store])!;
        Assert.Contains(id, own);
        Assert.Contains("Queued", own);
        Assert.Contains("pending", own, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(id, other);
        Assert.Equal("No matching YouTube job was found for your account.", other);
        Assert.Equal(own, (string)method.Invoke(null, [42ul, null, store])!);
    }

    internal static (IMusicSearchActionCoordinator, MusicSearchSessionToken) Search(Fixture f)
    {
        var sessions = f.Provider.GetRequiredService<IMusicSearchSessionService>();
        var token = sessions.SaveResults(42, 12, "track", [MusicSearchResultPage.FromYouTube(new YouTubeTrackSuggestionDTO
        { VideoId = "abcdefghijk", Title = "Track", Url = "https://www.youtube.com/watch?v=abcdefghijk" })]);
        Assert.True(sessions.BindMessage(42, 12, 34, token));
        return (f.Provider.GetRequiredService<IMusicSearchActionCoordinator>(), token);
    }

    internal sealed class ExpiringInteraction
    {
        private TimeSpan _age;
        public int Acknowledgments;
        public int Commits;
        public void Advance(TimeSpan time) => _age += time;
        public Task Acknowledge(CancellationToken _) { Acknowledgments++; return Task.CompletedTask; }
        public Task Commit(MusicSearchActionResult result, CancellationToken _)
        {
            if (_age >= TimeSpan.FromMinutes(15)) throw new IOException("Discord interaction token expired");
            Commits++;
            return Task.CompletedTask;
        }
    }
}
