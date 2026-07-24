using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChizuChan.Services
{
    public sealed class LlmProviderOverrideState
    {
        private readonly object _lock = new();
        private string? _storePath;
        private string? _overrideProviderName;
        private ILogger<LlmProviderOverrideState>? _logger;

        public string? OverrideProviderName
        {
            get
            {
                lock (_lock)
                    return _overrideProviderName;
            }
            set
            {
                lock (_lock)
                    _overrideProviderName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }

        public bool HasOverride => !string.IsNullOrWhiteSpace(OverrideProviderName);

        public void UseStore(string? storePath, ILogger<LlmProviderOverrideState>? logger = null)
        {
            _logger = logger;
            if (string.IsNullOrWhiteSpace(storePath))
                return;

            _storePath = Path.IsPathRooted(storePath)
                ? storePath
                : Path.Combine(AppContext.BaseDirectory, storePath);

            Load();
        }

        public void SetOverride(string providerName)
        {
            OverrideProviderName = providerName;
            Save();
        }

        public void ClearOverride()
        {
            OverrideProviderName = null;
            Save();
        }

        private void Load()
        {
            if (_storePath is null || !File.Exists(_storePath))
                return;

            try
            {
                var json = File.ReadAllText(_storePath);
                var state = JsonSerializer.Deserialize<PersistedOverrideState>(json);
                OverrideProviderName = state?.OverrideProviderName;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[LLM] Failed to load provider override from {Path}", _storePath);
            }
        }

        private void Save()
        {
            if (_storePath is null)
                return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
                var json = JsonSerializer.Serialize(
                    new PersistedOverrideState { OverrideProviderName = OverrideProviderName },
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storePath, json);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[LLM] Failed to persist provider override to {Path}", _storePath);
            }
        }

        private sealed class PersistedOverrideState
        {
            public string? OverrideProviderName { get; set; }
        }
    }
}
