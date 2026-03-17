using ChizuChan.Adapters;
using ChizuChan.Services.Interfaces;
using ChizuChan.Services.Media;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Logging;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using System;
using System.IO;
using System.Threading.Tasks;
using static ChizuChan.Services.Interfaces.Track;

namespace ChizuChan.Commands
{
    public class PlayLocalCommandModule : ApplicationCommandModule<ApplicationCommandContext>
    {
        private readonly IVoiceService _voiceService;
        private readonly IGuildService _guildService;
        private readonly IEmbedService _embedService;
        private readonly ILogger<PlayLocalCommandModule> _logger;
        private readonly GatewayClient _gatewayClient;
        private readonly IMusicUiState _uiState;
        private readonly RestClient _restClient;
        private readonly IVoiceClientRegistry _vcRegistry;

        public PlayLocalCommandModule(
            IVoiceService voiceService,
            IGuildService guildService,
            IEmbedService embedService,
            ILogger<PlayLocalCommandModule> logger,
            GatewayClient gatewayClient,
            RestClient restClient,
            IMusicUiState uiState,
            IVoiceClientRegistry vcRegistry)
        {
            _voiceService = voiceService;
            _guildService = guildService;
            _embedService = embedService;
            _logger = logger;
            _gatewayClient = gatewayClient;
            _uiState = uiState;
            _restClient = restClient;
            _vcRegistry = vcRegistry;
        }

        [SlashCommand("playlocal", "Plays a local audio file from the host machine.", Contexts = [InteractionContextType.Guild])]
        public async Task PlayLocalAsync(
            [SlashCommandParameter(Name = "path", Description = "Absolute path to the audio file on the host machine.")]
            string filePath)
        {
            await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

            if (!File.Exists(filePath))
            {
                await ModifyResponseAsync(m => m.Content = $"File not found: `{filePath}`");
                return;
            }

            // Restrict to audio file extensions as a basic safety check
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string[] allowedExtensions = [".mp3", ".flac", ".wav", ".ogg", ".aac", ".m4a", ".opus", ".wma", ".mp4", ".mkv", ".webm"];
            if (Array.IndexOf(allowedExtensions, ext) == -1)
            {
                await ModifyResponseAsync(m => m.Content = $"Unsupported file type `{ext}`. Supported: {string.Join(", ", allowedExtensions)}");
                return;
            }

            ulong guildId = Context.Guild!.Id;
            ulong userId = Context.User.Id;

            Guild guild = _guildService.GetGuild(guildId) ?? Context.Guild!;
            _guildService.AddOrUpdateGuild(guild);

            // Resolve the voice channel the user is in
            ulong voiceChannelId;
            if (_gatewayClient.Cache.Guilds.TryGetValue(guildId, out var cachedGuild) &&
                cachedGuild.VoiceStates.TryGetValue(userId, out var vs) &&
                vs.ChannelId is ulong vcId)
            {
                voiceChannelId = vcId;
            }
            else
            {
                var snap = _guildService.GetGuild(guildId) ?? Context.Guild!;
                if (!(snap.VoiceStates.TryGetValue(userId, out var vsSnap) && vsSnap.ChannelId is ulong vcId2))
                {
                    await ModifyResponseAsync(m => m.Content = "Join a voice channel first.");
                    return;
                }
                voiceChannelId = vcId2;
            }

            // Ensure channel has at least one non-bot user
            bool channelHasSomeone =
                _gatewayClient.Cache.Guilds.TryGetValue(guildId, out var g) &&
                g.VoiceStates.Values.Any(s => s.ChannelId == voiceChannelId && s.UserId != _gatewayClient.Id);

            if (!channelHasSomeone)
            {
                await ModifyResponseAsync(m => m.Content = "That voice channel is empty.");
                return;
            }

            // Block if bot is already in a different channel
            ulong botId = _gatewayClient.Id;
            if (_gatewayClient.Cache.Guilds.TryGetValue(guildId, out var g2) &&
                g2.VoiceStates.TryGetValue(botId, out var botVs) &&
                botVs.ChannelId is ulong botChannelId &&
                botChannelId != voiceChannelId)
            {
                await ModifyResponseAsync(m => m.Content = "I am already playing in another channel.");
                return;
            }

            var preSnap = await _voiceService.GetSnapshotAsync(guildId);

            if (!preSnap.IsConnected)
            {
                bool joined = await _voiceService.JoinAsync(
                    guildId: guildId,
                    voiceChannelId: voiceChannelId,
                    connect: async (ct) =>
                    {
                        _logger.LogInformation("[Connect] Calling JoinVoiceChannelAsync guild={GuildId} channel={ChannelId}", guildId, voiceChannelId);
                        var vcConfig = new VoiceClientConfiguration
                        {
                            Logger = new MicrosoftLoggerVoiceAdapter(_logger),
                            ReceiveHandler = new VoiceReceiveHandler(),
                        };
                        VoiceClient vc = await _gatewayClient.JoinVoiceChannelAsync(guildId, voiceChannelId, vcConfig);
                        _logger.LogInformation("[Connect] Calling vc.StartAsync...");
                        await vc.StartAsync(ct);
                        _logger.LogInformation("[Connect] vc.StartAsync returned, handing off to adapter (readiness polled in OpenPcmSinkAsync).");
                        _vcRegistry.Register(guildId, vc);
                        return new NetCordVoiceConnectionAdapter(vc, _logger);
                    });

                if (!joined)
                {
                    await ModifyResponseAsync(m => m.Content = "Failed to join voice.");
                    return;
                }
            }

            bool wasIdle = preSnap.Current is null;
            string displayTitle = Path.GetFileNameWithoutExtension(filePath);

            var track = new Track(displayTitle, TrackSourceType.StreamFactory)
            {
                RequestedByUserId = Context.User.Id,
                StreamFactory = ct => YtDlpFfmpeg.OpenPcmFromFileAsync(filePath, ct),
            };

            await _voiceService.EnqueueAsync(guildId, track);

            if (wasIdle)
            {
                var snapAfter = await _voiceService.GetSnapshotAsync(guildId);

                var (embed, components) = _embedService.BuildMusicPlayerEmbed(
                    title: displayTitle,
                    sourceUrl: null,
                    requestedBy: Context.User,
                    isPaused: false,
                    canSkip: snapAfter.CanSkip,
                    position: TimeSpan.Zero,
                    duration: null,
                    thumbnailUrl: null);

                RestMessage publicMsg = await _restClient.SendMessageAsync(
                    Context.Channel.Id,
                    new MessageProperties { Content = null, Embeds = new[] { embed }, Components = components });

                _uiState.SetNowPlayingMessage(guildId, publicMsg.ChannelId, publicMsg.Id);
                await ModifyResponseAsync(m => m.Content = "Started playback.");
            }
            else
            {
                var queued = _embedService.BuildQueuedConfirmationEmbed(
                    displayTitle: displayTitle,
                    sourceUrl: null,
                    requestedBy: Context.User);

                await ModifyResponseAsync(m =>
                {
                    m.Content = null;
                    m.Embeds = new[] { queued };
                    m.Components = Array.Empty<IMessageComponentProperties>();
                });

                if (_uiState.TryGetNowPlayingMessage(guildId, out var msgRef))
                {
                    var snap = await _voiceService.GetSnapshotAsync(guildId);
                    if (snap.Current is not null)
                    {
                        User requestedBy = Context.User;
                        if (snap.Current.RequestedByUserId is ulong uid)
                        {
                            try { requestedBy = await _restClient.GetUserAsync(uid); } catch { }
                        }

                        var pos = await _voiceService.GetPositionAsync(guildId);

                        var (embed, components) = _embedService.BuildMusicPlayerEmbed(
                            title: snap.Current.Title ?? "Now Playing",
                            sourceUrl: snap.Current.Url,
                            requestedBy: requestedBy,
                            isPaused: snap.IsPaused,
                            canSkip: snap.CanSkip,
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
