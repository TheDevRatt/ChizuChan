using ChizuChan.Services;
using ChizuChan.Services.Interfaces;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ComponentInteractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static ChizuChan.Services.Interfaces.Track;

namespace ChizuChan.Commands.Controllers
{
    /// <summary>
    /// Handles music control buttons (Skip, Pause, Resume, Stop) and updates the player embed in-place.
    /// </summary>
    public class MusicControllerModule : ComponentInteractionModule<ComponentInteractionContext>
    {
        private readonly IVoiceService _voiceService;
        private readonly IGuildService _guildService;
        private readonly IEmbedService _embedService;
        private readonly RestClient _restClient;
        private readonly IMusicUiState _ui;
        private readonly GatewayClient _gatewayClient;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, SemaphoreSlim> _editGates
    = new System.Collections.Concurrent.ConcurrentDictionary<ulong, SemaphoreSlim>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, CancellationTokenSource> _tickers
    = new System.Collections.Concurrent.ConcurrentDictionary<ulong, CancellationTokenSource>();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, CancellationTokenSource> _idleTimers
            = new System.Collections.Concurrent.ConcurrentDictionary<ulong, CancellationTokenSource>();

        private static SemaphoreSlim Gate(ulong g) => _editGates.GetOrAdd(g, _ => new SemaphoreSlim(1, 1));


        public MusicControllerModule(
        IVoiceService voiceService,
        IGuildService guildService,
        IEmbedService embedService,
        GatewayClient gatewayClient,
        RestClient restClient,
        IMusicUiState ui)
        {
            _voiceService = voiceService;
            _guildService = guildService;
            _embedService = embedService;
            _restClient = restClient;
            _ui = ui;
            _gatewayClient = gatewayClient;
        }


        [ComponentInteraction("music_skip")]
        public async Task SkipAsync()
            => await WithGuildAsync(async guildId =>
            {
                // Don’t try to edit the original component message; acknowledge ephemerally
                await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));

                // What was playing before we skip?
                var before = await _voiceService.GetSnapshotAsync(guildId);
                var prev = before.Current;

                await _voiceService.SkipAsync(guildId);

                // Wait briefly until VoiceService exposes a different Current
                await UpdatePlayerMessageAsync(guildId, prev);

                await ModifyResponseAsync(m =>
                {
                    m.Content = "Skipped.";
                    m.Embeds = Array.Empty<EmbedProperties>();
                    m.Components = Array.Empty<IComponentProperties>();
                });
            });


        [ComponentInteraction("music_pause")]
        public async Task PauseAsync()
            => await WithGuildAsync(async guildId =>
            {
                await RespondAsync(InteractionCallback.DeferredModifyMessage);
                await _voiceService.PauseAsync(guildId);
                await UpdatePlayerMessageAsync(guildId);
            });


        [ComponentInteraction("music_resume")]
        public async Task ResumeAsync()
            => await WithGuildAsync(async guildId =>
            {
                await RespondAsync(InteractionCallback.DeferredModifyMessage);
                await _voiceService.ResumeAsync(guildId);
                await UpdatePlayerMessageAsync(guildId);
            });


        [ComponentInteraction("music_stop")]
        public async Task StopAsync()
            => await WithGuildAsync(async guildId =>
            {
                await RespondAsync(InteractionCallback.DeferredModifyMessage);

                await _voiceService.StopAsync(guildId);
                await _voiceService.LeaveAsync(guildId);

                // Make absolutely sure the bot leaves the channel at the gateway level:
                await _gatewayClient.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, null));

                if (_ui.TryGetNowPlayingMessage(guildId, out var msgRef))
                {
                    await _restClient.ModifyMessageAsync(
                        msgRef.ChannelId,
                        msgRef.MessageId,
                        m =>
                        {
                            m.Content = "Stopped.";
                            m.Embeds = Array.Empty<EmbedProperties>();
                            m.Components = Array.Empty<IComponentProperties>();
                        });
                    _ui.ClearNowPlayingMessage(guildId);
                }
            });


        // ---- Helpers ----


        private async Task WithGuildAsync(Func<ulong, Task> action)
        {
            if (Context.Guild is null)
            {
                await RespondAsync(InteractionCallback.Message("This control is only available in servers."));
                return;
            }
            await action(Context.Guild.Id);
        }

        private async Task UpdatePlayerMessageAsync(ulong guildId, Track? prev = null)
        {
            if (!_ui.TryGetNowPlayingMessage(guildId, out var msgRef))
                return;

            // If prev != null, we’re coming from Skip: wait until Current changes
            Track? current = null;
            bool isPaused = false;
            bool canSkip = false;

            if (prev is not null)
            {
                const int attempts = 40;     // ~6s total
                const int delayMs = 150;
                for (int i = 0; i < attempts; i++)
                {
                    var snap = await _voiceService.GetSnapshotAsync(guildId);
                    current = snap.Current;
                    isPaused = snap.IsPaused;
                    canSkip = snap.CanSkip;

                    if (current is not null && !IsSameTrack(current, prev))
                        break;

                    await Task.Delay(delayMs);
                }

                // Still the same (or nothing) — leave the old message untouched.
                if (current is null || IsSameTrack(current, prev))
                    return;
            }
            else
            {
                var snap = await _voiceService.GetSnapshotAsync(guildId);
                current = snap.Current;
                isPaused = snap.IsPaused;
                canSkip = snap.CanSkip;
            }

            if (current is null)
            {
                await _restClient.ModifyMessageAsync(msgRef.ChannelId, msgRef.MessageId, m =>
                {
                    m.Content = "Nothing is playing.";
                    m.Embeds = Array.Empty<EmbedProperties>();
                    m.Components = Array.Empty<IComponentProperties>();
                });
                return;
            }

            var pos = await _voiceService.GetPositionAsync(guildId);

            // Try to show the original requester (best effort)
            User requestedBy = Context.User;
            if (current.RequestedByUserId is ulong uid)
            {
                try { requestedBy = await _restClient.GetUserAsync(uid); } catch { }
            }

            var (embed, comps) = _embedService.BuildMusicPlayerEmbed(
                title: current.Title ?? "Now Playing",
                sourceUrl: current.Url,
                requestedBy: requestedBy,
                isPaused: isPaused,
                position: pos,
                duration: current.Duration,
                thumbnailUrl: current.ThumbnailUrl,
                canSkip: canSkip);

            // Normalize components to the exact type REST expects
            IComponentProperties[] components =
                comps as IComponentProperties[] ??
                (comps is IEnumerable<IComponentProperties> e ? e.ToArray() : Array.Empty<IComponentProperties>());

            await _restClient.ModifyMessageAsync(msgRef.ChannelId, msgRef.MessageId, m =>
            {
                m.Content = null;
                m.Embeds = new[] { embed };
                m.Components = components;
            });
        }

        // Stable equality to detect “same track”
        private static bool IsSameTrack(Track a, Track? b)
        {
            if (b is null) return false;
            if (!string.IsNullOrEmpty(a.Url) && !string.IsNullOrEmpty(b.Url))
                return string.Equals(a.Url, b.Url, StringComparison.OrdinalIgnoreCase);
            return string.Equals(a.Title ?? "", b.Title ?? "", StringComparison.Ordinal)
                && Nullable.Equals(a.Duration, b.Duration);
        }
    }
}
