namespace ChizuChan.Options
{
    public enum LlmProviderKind
    {
        Ollama,
        OpenAICompatible
    }

    public class LlmProviderOptions
    {
        public string Name { get; set; } = string.Empty;
        public LlmProviderKind Kind { get; set; } = LlmProviderKind.OpenAICompatible;
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string ApiKeyEnvironmentVariable { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public bool SupportsVision { get; set; }
        public int Priority { get; set; }
        public int DailyRequestLimit { get; set; }
        public int RequestsPerMinuteLimit { get; set; }
        public int DailyTokenLimit { get; set; }
        public int CooldownSecondsAfterRateLimit { get; set; } = 300;
        public Dictionary<string, string> Headers { get; set; } = new();

        public string EffectiveName => string.IsNullOrWhiteSpace(Name) ? Model : Name;

        public string? ResolveApiKey()
        {
            if (!string.IsNullOrWhiteSpace(ApiKey))
                return ApiKey;

            if (!string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable))
                return Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);

            return null;
        }
    }
}
