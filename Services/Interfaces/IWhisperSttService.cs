namespace ChizuChan.Services.Interfaces
{
    /// <summary>
    /// Transcribes a WAV audio buffer to text using Whisper.
    /// </summary>
    public interface IWhisperSttService
    {
        /// <summary>
        /// Transcribes the provided WAV-format bytes. Returns an empty string if nothing was heard.
        /// </summary>
        Task<string> TranscribeAsync(byte[] wavBytes, CancellationToken ct = default);
    }
}
