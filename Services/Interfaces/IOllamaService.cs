namespace ChizuChan.Services.Interfaces
{
    public interface IOllamaService
    {
        /// <param name="userMessage">The message to respond to (mentions already resolved to usernames).</param>
        /// <param name="contextMessages">Recent channel messages for context.</param>
        /// <param name="requireResponse">
        /// When false the model decides whether it is actually being addressed.
        /// Returns null if the model chooses to ignore.
        /// </param>
        /// <param name="imageUrls">
        /// Optional image URLs to include (vision models only).
        /// The service downloads and base64-encodes them before sending.
        /// </param>
        Task<string?> GenerateAsync(
            string userMessage,
            IList<(string Author, string Content, bool IsBot)> contextMessages,
            bool requireResponse,
            IList<string>? imageUrls = null);
    }
}
