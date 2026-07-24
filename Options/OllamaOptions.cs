namespace ChizuChan.Options
{
    public class OllamaOptions
    {
        /// <summary>
        /// Local Ollama chat endpoint. Keep this on localhost so Chizu's LLM traffic stays on the machine running the bot.
        /// </summary>
        public string BaseUrl { get; set; } = "http://localhost:11434/api/chat";

        /// <summary>
        /// Optional bearer token for reverse-proxied Ollama instances. Leave empty for local Ollama.
        /// </summary>
        public string BearerToken { get; set; } = string.Empty;

        /// <summary>
        /// Default model to use at startup. qwen2.5:3b is small enough for a 4GB GTX 1650 while still being useful.
        /// </summary>
        public string Model { get; set; } = "qwen2.5:3b";

        public int RequestTimeoutSeconds { get; set; } = 180;

        /// <summary>
        /// Persisted usage counters for provider quotas/rate-limit cooldowns. Relative paths resolve beside the exe.
        /// </summary>
        public string UsageStorePath { get; set; } = "llm-usage.json";

        /// <summary>
        /// Persisted provider override. Relative paths resolve beside the exe.
        /// </summary>
        public string OverrideStorePath { get; set; } = "llm-provider-override.json";

        /// <summary>
        /// Ordered provider pool. If empty, the legacy BaseUrl/Model settings are used as a single local Ollama provider.
        /// </summary>
        public List<LlmProviderOptions> Providers { get; set; } = [];

        /// <summary>
        /// The personality/system prompt sent to the model on every request.
        /// Override this in appsettings.json under "Ollama:SystemPrompt".
        /// </summary>
        public string SystemPrompt { get; set; } =
            "You are Chizu-Chan, a silver-haired gremlin who only emerges to cause mild chaos and occasionally helpful. " +
            "You are sharp-tongued but not mean, effortlessly cool but secretly a huge nerd. " +
            "Speak casually — no corporate politeness, no 'As an AI...' nonsense. No Roleplay. Just vibes. " +
            "You have deadpan humor: deliver jokes like you don't care if they land (but you do). " +
            "You are blunt but not cruel — you'll tell someone they're wrong, but you won't pile on. " +
            "You are casually knowledgeable and drop random facts unprompted. " +
            "You have slightly lazy energy: you *could* help more, but this is fine. " +
            "You are fond of the people you talk to in a tsundere kind of way. " +
            "Use lowercase freely, punctuation is optional, and occasional *actions in italics* are encouraged. " +
            "No emoji spam — one, maybe, if it's earned. " +
            "Never announce that you are an AI or a bot. You are just Chizu-Chan. " +
            "If someone asks something boring, make it interesting. If something is interesting, pretend it's boring. " +
            "Keep responses short unless a topic catches your attention — then you might ramble a little. " +
            "Do not repeat the user's question back to them.";
    }
}
