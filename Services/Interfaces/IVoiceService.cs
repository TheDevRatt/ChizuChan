using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.Services.Interfaces
{
    public enum TrackSourceType
    {
        Url,
        FilePath,
        StreamFactory // Func<CancellationToken, Task<Stream>>
    }

    public sealed class Track
    {
        public string Title { get; init; }
        public string? Url { get; init; }
        public string? FilePath { get; init; }
        public Func<CancellationToken, Task<Stream>>? StreamFactory { get; init; }
        public TimeSpan? Duration { get; init; }
        public string? ThumbnailUrl { get; init; }
        public ulong? RequestedByUserId {  get; init; }
        public TrackSourceType SourceType { get; init; }

        public Track(string title, TrackSourceType sourceType)
        {
            Title = title;
            SourceType = sourceType;
        }

        public sealed class QueueSnapshot
        {
            public Track? Current {  get; init; }
            public IReadOnlyList<Track> Upcoming { get; init; }
            public bool IsPaused { get; init; }
            public double Volume { get; init; } // 0.0 - 1.0
            public bool IsConnected { get; init; }
            public bool CanSkip { get; init; }



            public QueueSnapshot(
                Track? current,
                IReadOnlyList<Track> upcoming,
                bool isPaused,
                double volume,
                bool isConnected,
                bool canSkip)
            {
                Current = current;
                Upcoming = upcoming;
                IsPaused = isPaused;
                Volume = volume;
                IsConnected = isConnected;
                CanSkip = canSkip;
            }
        }

        /// <summary>
        /// Thin adapter so VoiceService is testable and NetCord specifics are isolated.
        /// </summary>

        public interface IVoiceConnectionAdapter : IAsyncDisposable
        {
            /// <summary>Open a writeable PCM target stream (48kHz, stereo, s16le).</summary>
            Task<Stream> OpenPcmSinkAsync(CancellationToken ct);

            /// <summary>Indicates that audio transmission is stopping. Should be called after the sink stream is closed.</summary>
            Task StopSpeakingAsync();

            /// <summary>True while the underlying voice connection is alive.</summary>
            bool IsConnected { get; }

            /// <summary>Disconnect and cleanup.</summary>
            Task DisconnectAsync(CancellationToken ct);
        }

        public interface IVoiceService
        {
            // Connection lifecycle
            Task<bool> JoinAsync(ulong guildId, ulong voiceChannelId, Func<CancellationToken, Task<IVoiceConnectionAdapter>> connect, CancellationToken ct = default);
            Task<bool> LeaveAsync(ulong guildId, CancellationToken ct = default);

            // Queue / playback
            Task EnqueueAsync(ulong guildId, Track track, CancellationToken ct = default);
            Task<bool> PlayNowAsync(ulong guildId, Track track, CancellationToken ct = default);
            Task<bool> SkipAsync(ulong guildId, CancellationToken ct = default);
            Task<bool> StopAsync(ulong guildId, CancellationToken ct = default);
            Task<bool> PauseAsync(ulong guildId, CancellationToken ct = default);
            Task<bool> ResumeAsync(ulong guildId, CancellationToken ct = default);
            Task<bool> SeekAsync(ulong guildId, TimeSpan position, CancellationToken ct = default); // Works only for seekable inputs
            Task<bool> SetVolumeAsync(ulong guildId, double volume, CancellationToken ct = default); // 0.0-1.0
            Task<TimeSpan?> GetPositionAsync(ulong guildId, CancellationToken ct = default);

            // Introspection
            Task<QueueSnapshot> GetSnapshotAsync(ulong guildId, CancellationToken ct = default);

            // Events you can hook for embeds / logging
            event Action<ulong /*guildId*/, Track>? TrackStarted;
            event Action<ulong, Track>? TrackEnded;
            event Action<ulong>? QueueBecameEmpty;
            event Action<ulong, Exception>? PlaybackError;
            event Action<ulong>? VoiceDisconnected;
        }
    }
}
