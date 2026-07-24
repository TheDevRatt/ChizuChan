# ChizuChan multi-provider LLM routing

ChizuChan can now route LLM requests through an ordered provider pool and automatically fall back when a provider is out of quota, rate-limited, missing an API key, or returning errors.

The default recommended chain is:

1. Groq fast model, high-volume free tier
2. Groq quality model, lower daily cap but better quality
3. OpenRouter free router
4. Local Ollama fallback

## API keys

Use environment variables so secrets do not need to live in `appsettings.json`.

PowerShell, current session only:

```powershell
$env:GROQ_API_KEY = "gsk_your_key_here"
$env:OPENROUTER_API_KEY = "sk-or-your_key_here"
```

PowerShell, persist for future sessions:

```powershell
setx GROQ_API_KEY "gsk_your_key_here"
setx OPENROUTER_API_KEY "sk-or-your_key_here"
```

After `setx`, close and reopen PowerShell before starting ChizuChan.

If no online keys are configured, Chizu automatically skips online providers and uses local Ollama.

## Provider config

`appsettings.json` still uses the existing `Ollama` section for compatibility, but it can now contain multiple providers:

```json
"Ollama": {
  "BaseUrl": "http://localhost:11434/api/chat",
  "BearerToken": "",
  "Model": "qwen2.5:3b",
  "RequestTimeoutSeconds": 180,
  "UsageStorePath": "llm-usage.json",
  "OverrideStorePath": "llm-provider-override.json",
  "Providers": [
    {
      "Name": "groq-fast",
      "Kind": "OpenAICompatible",
      "BaseUrl": "https://api.groq.com/openai/v1/chat/completions",
      "ApiKeyEnvironmentVariable": "GROQ_API_KEY",
      "Model": "llama-3.1-8b-instant",
      "Priority": 10,
      "DailyRequestLimit": 14400,
      "RequestsPerMinuteLimit": 30,
      "DailyTokenLimit": 500000,
      "CooldownSecondsAfterRateLimit": 300
    },
    {
      "Name": "groq-quality",
      "Kind": "OpenAICompatible",
      "BaseUrl": "https://api.groq.com/openai/v1/chat/completions",
      "ApiKeyEnvironmentVariable": "GROQ_API_KEY",
      "Model": "llama-3.3-70b-versatile",
      "Priority": 20,
      "DailyRequestLimit": 1000,
      "RequestsPerMinuteLimit": 30,
      "DailyTokenLimit": 100000,
      "CooldownSecondsAfterRateLimit": 300
    },
    {
      "Name": "openrouter-free",
      "Kind": "OpenAICompatible",
      "BaseUrl": "https://openrouter.ai/api/v1/chat/completions",
      "ApiKeyEnvironmentVariable": "OPENROUTER_API_KEY",
      "Model": "openrouter/free",
      "Priority": 30,
      "DailyRequestLimit": 50,
      "RequestsPerMinuteLimit": 20,
      "CooldownSecondsAfterRateLimit": 300,
      "Headers": {
        "HTTP-Referer": "https://github.com/TheDevRatt/ChizuChan",
        "X-Title": "ChizuChan"
      }
    },
    {
      "Name": "local-ollama",
      "Kind": "Ollama",
      "BaseUrl": "http://localhost:11434/api/chat",
      "Model": "qwen2.5:3b",
      "Priority": 100
    }
  ]
}
```

## How routing works

For every LLM request, Chizu:

1. Sorts providers by `Priority`.
2. Skips disabled providers.
3. Skips OpenAI-compatible providers with no API key.
4. Skips providers whose local tracker says they hit daily/minute token/request caps.
5. Tries the first available provider.
6. If it returns HTTP 429, records a rate-limit failure and cooldown, then tries the next provider.
7. If it returns another error, records an error failure, then tries the next provider.
8. If every online provider is unavailable, falls back to local Ollama.

## Built-in tracker

Usage is tracked in `llm-usage.json` beside the exe by default.

Tracked per provider:

- requests today
- tokens today, when the provider reports usage
- rate-limit failures today
- other errors today
- current minute request window
- cooldown after HTTP 429

Delete `llm-usage.json` if you ever want to reset Chizu's local counters manually.

## Discord commands

Use:

```text
/llm_status
```

It shows each configured provider, the active override, whether API keys are present, whether the tracker thinks it is available, requests today, token usage, and cooldown state.

Use:

```text
/llm_provider
```

to switch between:

- Auto Routing
- Quality: Groq Llama 3.3 70B
- Fast: Groq Llama 3.1 8B
- OpenRouter Free
- Local Ollama

Provider override persists in `llm-provider-override.json` beside the exe.

## Notes

- Groq and OpenRouter are OpenAI-compatible, so they use `choices[0].message.content` responses.
- Ollama uses `message.content` responses.
- The code supports both response formats.
- Local Ollama should remain last because it has unlimited local requests but lower quality than the hosted models.
