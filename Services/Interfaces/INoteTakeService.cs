using NetCord.Gateway.Voice;

namespace ChizuChan.Services.Interfaces
{
    /// <summary>
    /// Manages per-guild voice listeners that transcribe speech to a rolling text log
    /// without AI responses — intended for D&D session note-taking.
    /// </summary>
    public interface INoteTakeService
    {
        /// <summary>
        /// Begins recording speech in <paramref name="voiceClient"/> for the given guild.
        /// Transcripts are posted to <paramref name="textChannelId"/> and written to a log file.
        /// </summary>
        void StartRecording(ulong guildId, ulong textChannelId, VoiceClient voiceClient);

        bool IsRecording(ulong guildId);

        /// <summary>Returns the path to the current session log file, or null if not recording.</summary>
        string? GetLogPath(ulong guildId);

        void StopRecording(ulong guildId);
    }
}
