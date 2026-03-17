using ChizuChan.Adapters;
using ChizuChan.Services.Interfaces;
using Microsoft.Extensions.Logging;
using static ChizuChan.Services.Interfaces.Track;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Logging;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace ChizuChan.Commands
{
    public class VoiceAiCommandModule : ApplicationCommandModule<ApplicationCommandContext>
    {
        private readonly IVoiceInputService _voiceInput;
        private readonly IVoiceService _voiceService;
        private readonly IVoiceClientRegistry _vcRegistry;
        private readonly IGuildService _guildService;
        private readonly GatewayClient _gatewayClient;
        private readonly ILogger<VoiceAiCommandModule> _logger;

        public VoiceAiCommandModule(
            IVoiceInputService voiceInput,
            IVoiceService voiceService,
            IVoiceClientRegistry vcRegistry,
            IGuildService guildService,
            GatewayClient gatewayClient,
            ILogger<VoiceAiCommandModule> logger)
        {
            _voiceInput = voiceInput;
            _voiceService = voiceService;
            _vcRegistry = vcRegistry;
            _guildService = guildService;
            _gatewayClient = gatewayClient;
            _logger = logger;
        }

        [SlashCommand("listen", "Start voice AI mode — speak and Chizu will respond.", Contexts = [InteractionContextType.Guild])]
        public async Task ListenAsync()
        {
            await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

            ulong guildId = Context.Guild!.Id;
            ulong userId = Context.User.Id;
            ulong textChannelId = Context.Channel.Id;

            if (_voiceInput.IsListening(guildId))
            {
                await ModifyResponseAsync(m => m.Content = "Already listening. Use `/stoplisten` to stop.");
                return;
            }

            // Find which voice channel the user is in
            ulong voiceChannelId;
            if (_gatewayClient.Cache.Guilds.TryGetValue(guildId, out var cachedGuild) &&
                cachedGuild.VoiceStates.TryGetValue(userId, out var vs) &&
                vs.ChannelId is ulong vcId)
            {
                voiceChannelId = vcId;
            }
            else
            {
                await ModifyResponseAsync(m => m.Content = "Join a voice channel first.");
                return;
            }

            // If the bot already has an active VoiceClient (music is playing), reuse it
            if (_vcRegistry.TryGet(guildId, out var existingVc) && existingVc is not null)
            {
                _voiceInput.StartListening(guildId, textChannelId, existingVc);
                await ModifyResponseAsync(m => m.Content = "🎙️ Now listening! Speak and I'll respond.");
                return;
            }

            // Not connected — join voice for listen-only mode
            VoiceClient? newVc = null;

            Guild guild = _guildService.GetGuild(guildId) ?? Context.Guild!;
            _guildService.AddOrUpdateGuild(guild);

            bool joined = await _voiceService.JoinAsync(
                guildId: guildId,
                voiceChannelId: voiceChannelId,
                connect: async (ct) =>
                {
                    _logger.LogInformation("[VoiceAI] Joining guild={GuildId} channel={ChannelId}", guildId, voiceChannelId);
                    var vcConfig = new VoiceClientConfiguration
                    {
                        Logger = new MicrosoftLoggerVoiceAdapter(_logger),
                        ReceiveHandler = new VoiceReceiveHandler(),
                    };
                    newVc = await _gatewayClient.JoinVoiceChannelAsync(guildId, voiceChannelId, vcConfig);
                    newVc.Connect += () => { _logger.LogInformation("[VoiceAI] Voice connected"); return default; };
                    await newVc.StartAsync(ct);
                    _vcRegistry.Register(guildId, newVc);
                    return new NetCordVoiceConnectionAdapter(newVc, _logger);
                });

            if (!joined || newVc is null)
            {
                await ModifyResponseAsync(m => m.Content = "Failed to join voice channel.");
                return;
            }

            _voiceInput.StartListening(guildId, textChannelId, newVc);
            await ModifyResponseAsync(m => m.Content = "🎙️ Joined voice and started listening! Speak and I'll respond.");
        }

        [SlashCommand("stoplisten", "Stop voice AI mode.", Contexts = [InteractionContextType.Guild])]
        public async Task StopListenAsync()
        {
            await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

            ulong guildId = Context.Guild!.Id;

            if (!_voiceInput.IsListening(guildId))
            {
                await ModifyResponseAsync(m => m.Content = "Not currently in voice AI mode.");
                return;
            }

            _voiceInput.StopListening(guildId);
            await ModifyResponseAsync(m => m.Content = "🔇 Stopped listening.");
        }
    }
}
