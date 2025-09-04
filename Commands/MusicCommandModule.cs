using ChizuChan.Adapters;
using ChizuChan.Services.Interfaces;
using ChizuChan.Services.Media;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using System;
using System.Threading.Tasks;
using static ChizuChan.Services.Interfaces.Track;
using static ChizuChan.Services.Media.YtDlpFfmpeg;

namespace ChizuChan.Commands
{
    public class MusicCommandModule : ApplicationCommandModule<ApplicationCommandContext>
    {
        private readonly IVoiceService _voiceService;
        private readonly IGuildService _guildService;
        private readonly IEmbedService _embedService;
        private readonly ILogger<MusicCommandModule> _logger;
        private readonly GatewayClient _gatewayClient;
        private readonly IMusicUiState _uiState;
        private readonly RestClient _restClient;

        public MusicCommandModule(
            IVoiceService voiceService,
            IGuildService guildService,
            IEmbedService embedService,
            ILogger<MusicCommandModule> logger,
            GatewayClient gatewayClient,
            RestClient restClient,
            IMusicUiState uiState)
        {
            _voiceService = voiceService;
            _guildService = guildService;
            _embedService = embedService;
            _logger = logger;
            _gatewayClient = gatewayClient;
            _uiState = uiState;
            _restClient = restClient;
        }

        [SlashCommand("play", "Plays a YouTube (or direct) audio URL.", Contexts = [InteractionContextType.Guild])]
        public async Task PlayAsync(string url)
        {
            // Make the interaction response ephemeral so “queued” confirmations stay private
            await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

            Guild guild = _guildService.GetGuild(Context.Guild!.Id) ?? Context.Guild!;
            _guildService.AddOrUpdateGuild(guild);

            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                await ModifyResponseAsync(m => m.Content = "Please provide a valid absolute URL.");
                return;
            }

            // NEW (fresh from gateway cache; requires GuildVoiceStates intent)
            ulong guildId = Context.Guild!.Id;
            ulong userId = Context.User.Id;

            if (_gatewayClient.Cache.Guilds.TryGetValue(guildId, out var cachedGuild) &&
                cachedGuild.VoiceStates.TryGetValue(userId, out var vs) &&
                vs.ChannelId is ulong voiceChannelId)
            {
                // voiceChannelId is ready to use
            }
            else
            {
                // LAST RESORT: fall back to the interaction snapshot if cache not populated yet
                var snap = _guildService.GetGuild(guildId) ?? Context.Guild!;
                if (!(snap.VoiceStates.TryGetValue(userId, out var vsSnap) && vsSnap.ChannelId is ulong voiceChannelId2))
                {
                    await ModifyResponseAsync(m => m.Content = "Join a voice channel first.");
                    return;
                }
                voiceChannelId = voiceChannelId2;
            }

            // Optional: ensure the channel still has at least one non-bot user (defensive)
            bool channelHasSomeone =
                _gatewayClient.Cache.Guilds.TryGetValue(guildId, out var g) &&
                g.VoiceStates.Values.Any(s => s.ChannelId == voiceChannelId && s.UserId != _gatewayClient.Id);

            if (!channelHasSomeone)
            {
                await ModifyResponseAsync(m => m.Content = "That voice channel is empty.");
                return;
            }

            ulong botId = _gatewayClient.Id;

            if (_gatewayClient.Cache.Guilds.TryGetValue(guildId, out var g2) &&
                g2.VoiceStates.TryGetValue(botId, out var botVs) &&
                botVs.ChannelId is ulong botChannelId &&
                botChannelId != voiceChannelId)
            {
                await ModifyResponseAsync(m => m.Content = "I am already playing in another channel.");
                return;
            }

            // BEFORE you join:
            var preSnap = await _voiceService.GetSnapshotAsync(guild.Id);

            // Only join if not already connected in this guild
            if (!preSnap.IsConnected)
            {
                bool joined = await _voiceService.JoinAsync(
                    guildId: guild.Id,
                    voiceChannelId: voiceChannelId,
                    connect: async (ct) =>
                    {
                        VoiceClient vc = await _gatewayClient.JoinVoiceChannelAsync(guild.Id, voiceChannelId);
                        await vc.StartAsync();
                        return new NetCordVoiceConnectionAdapter(vc, _logger);
                    });

                if (!joined)
                {
                    await ModifyResponseAsync(m => m.Content = "Failed to join voice.");
                    return;
                }
            }

            bool wasIdle = preSnap.Current is null;

            // Resolve metadata (title/thumbnail/duration)
            YtDlpFfmpeg.MediaMeta meta;
            try { meta = await YtDlpFfmpeg.ResolveMetadataAsync(url, CancellationToken.None); }
            catch { meta = new(null, null, null); }

            var track = new Track(meta.Title ?? url, TrackSourceType.StreamFactory)
            {
                RequestedByUserId = Context.User.Id,
                StreamFactory = ct => YtDlpFfmpeg.OpenPcmFromUrlAsync(url, ct),
                Duration = meta.Duration,
                ThumbnailUrl = meta.Thumbnail,   // ⬅️ add this property to Track (see section C)
                Url = url,              // keep source for embeds
            };

            await _voiceService.EnqueueAsync(guild.Id, track);

            if (wasIdle)
            {
                var snapAfter = await _voiceService.GetSnapshotAsync(guild.Id);

                var (embed, components) = _embedService.BuildMusicPlayerEmbed(
                    title: meta.Title ?? "Now Playing",
                    sourceUrl: url,
                    requestedBy: Context.User,
                    isPaused: false,
                    canSkip: snapAfter.CanSkip,
                    position: TimeSpan.Zero,
                    duration: meta.Duration,
                    thumbnailUrl: meta.Thumbnail);

                RestMessage publicMsg = await _restClient.SendMessageAsync(
                    Context.Channel.Id,
                    new MessageProperties { Content = null, Embeds = new[] { embed }, Components = components });

                _uiState.SetNowPlayingMessage(guild.Id, publicMsg.ChannelId, publicMsg.Id);
                await ModifyResponseAsync(m => m.Content = "Started playback.");
            }
            else
            {
                // 1) Send the ephemeral “queued” confirmation
                var queued = _embedService.BuildQueuedConfirmationEmbed(
                    displayTitle: meta.Title ?? "Queued track",
                    sourceUrl: url,
                    requestedBy: Context.User);

                await ModifyResponseAsync(m =>
                {
                    m.Content = null;
                    m.Embeds = new[] { queued };
                    m.Components = Array.Empty<IComponentProperties>();
                });

                // 2) If we already have a public Now Playing message, refresh it so Skip becomes enabled
                if (_uiState.TryGetNowPlayingMessage(guild.Id, out var msgRef))
                {
                    var snap = await _voiceService.GetSnapshotAsync(guild.Id);
                    if (snap.Current is not null)
                    {
                        // Best-effort: show the original requester of the *current* track
                        User requestedBy = Context.User;
                        if (snap.Current.RequestedByUserId is ulong uid)
                        {
                            try { requestedBy = await _restClient.GetUserAsync(uid); } catch { }
                        }

                        var pos = await _voiceService.GetPositionAsync(guild.Id);

                        var (embed, components) = _embedService.BuildMusicPlayerEmbed(
                            title: snap.Current.Title ?? "Now Playing",
                            sourceUrl: snap.Current.Url,
                            requestedBy: requestedBy,
                            isPaused: snap.IsPaused,
                            canSkip: snap.CanSkip,                 // <-- this flips Skip to enabled
                            position: pos,
                            duration: snap.Current.Duration,
                            thumbnailUrl: snap.Current.ThumbnailUrl);

                        await _restClient.ModifyMessageAsync(msgRef.ChannelId, msgRef.MessageId, m =>
                        {
                            m.Content = null;
                            m.Embeds = new[] { embed };
                            m.Components = components;
                        });
                    }
                }
            }
        }
    }
}
